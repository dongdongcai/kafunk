#nowarn "40"
namespace Kafunk

open FSharp.Control
open System
open System.Net
open System.Threading
open Kafunk

/// A view of the cluster state.
type internal ClusterState = {
  bootstrapBroker : Broker option
  brokersByTopicPartition : Map<TopicName * Partition, Broker>
  brokersByGroup : Map<GroupId, Broker>
  brokerChansByNodeId : Map<NodeId, Chan>
  brokerChansByEndPoint : Map<EndPoint, Chan>
  brokersByNodeId : Map<NodeId, Broker>
  version : int
} with
  
  /// Returns an empty cluster state.
  static member Zero =
    {
      bootstrapBroker = None
      brokerChansByEndPoint = Map.empty
      brokersByTopicPartition = Map.empty
      brokersByGroup = Map.empty
      brokerChansByNodeId = Map.empty
      brokersByNodeId = Map.empty
      version = 0
    }

  /// Returns the set of topic-partitions in the cluster state.
  static member topicPartitions (s:ClusterState) =
    s.brokersByTopicPartition
    |> Seq.map (fun kvp -> kvp.Key)
    |> Seq.groupBy fst
    |> Seq.map (fun (tn,xs) -> tn, xs |> Seq.map snd |> Seq.toArray)
    |> Map.ofSeq

  /// Determines whether the cluster state contains metadata for the specified topics.
  static member containsTopicMetadata (ts:TopicName[]) (s:ClusterState) =
    let topics = s.brokersByTopicPartition |> Seq.map (fun kvp -> kvp.Key |> fst) |> set
    ts |> Seq.forall (fun x -> Set.contains x topics )

  /// Returns the broker channel for the coordinator for the specified group.
  static member containsGroupCoordinator (groupId:GroupId) (s:ClusterState) =
    s
    |> ClusterState.tryFindGroupCoordinatorBroker groupId
    |> Option.isSome

  /// Returns the broker channel for the coordinator for the specified group.
  static member tryFindGroupCoordinatorBroker (groupId:GroupId) (s:ClusterState) =
    s.brokersByGroup
    |> Map.tryFind groupId

  /// Returns the broker channel for the specified topic-partition.
  static member tryFindTopicPartitionBroker (tn:TopicName, p:Partition) (s:ClusterState) : Broker option =
    s.brokersByTopicPartition
    |> Map.tryFind (tn,p)

  /// Returns the broker channel for the specified topic-partition.
  static member tryFindBootstrapBroker (s:ClusterState) : Broker option =
    s.bootstrapBroker

  static member updateMetadata (brokers:Broker[], topicNodes:(TopicName * Partition * NodeId)[]) (s:ClusterState) =
    let brokersById = brokers |> Seq.map (fun b -> b.nodeId, b) |> Map.ofSeq
    let brokersByPartitions =
      topicNodes 
      |> Seq.choose (fun (t,p,nodeId) -> 
        match Map.tryFind nodeId brokersById with
        | Some b -> Some ((t,p),b)
        | None -> None)
    let removeTopicPartitions =
      topicNodes |> Seq.choose (fun (t,p,n) -> if n < 0 then Some (t,n) else None)        
    { s with
          brokersByNodeId = brokersById
          brokersByTopicPartition = 
            s.brokersByTopicPartition 
            |> Map.addMany brokersByPartitions
            |> Map.removeAll removeTopicPartitions
          version = s.version + 1 }

  static member updateGroupCoordinator (broker:Broker, gid:GroupId) (s:ClusterState) =
    { s with
        brokersByGroup = s.brokersByGroup |> Map.add gid broker
        version = s.version + 1 }

  static member updateBootstrapBroker (b:Broker) (s:ClusterState) =
    { s with
        bootstrapBroker = Some b
        version = s.version + 1 }

  /// Returns the broker channel for the specified endpoint.
  static member tryFindBrokerChanByEndPoint (ep:EndPoint) (s:ClusterState) =
    s.brokerChansByEndPoint |> Map.tryFind ep

  /// Returns the broker channel for the node id.
  static member tryFindBrokerChanById (nodeId:NodeId) (s:ClusterState) =
    s.brokerChansByNodeId |> Map.tryFind nodeId

  static member containsBrokerChan (s:ClusterState) (nodeId:NodeId) =
    match s.brokerChansByNodeId |> Map.tryFind nodeId with
    | Some _ -> true
    | _ -> false

  static member addBrokerChan (b:Broker, ch:Chan) (s:ClusterState)=
    {
      s with
        brokerChansByNodeId = s.brokerChansByNodeId |> Map.add b.nodeId ch
        brokerChansByEndPoint = s.brokerChansByEndPoint |> Map.add (Chan.endpoint ch) ch
        version = s.version + 1
    }
  
  static member removeBroker (b:Broker) (s:ClusterState) : Async<ClusterState> = async {    
    let! s = async {
      match ClusterState.tryFindBrokerChanById b.nodeId s with
      | Some ch -> 
        do! Chan.close ch
        let ep = Chan.endpoint ch
        return ClusterState.removeBrokerChan ep s
      | None ->
        return s }
    let groupIds = 
      s.brokersByGroup
      |> Seq.choose (fun kvp ->
        if kvp.Value = b then Some kvp.Key
        else None)
    let topicPartitions =
      s.brokersByTopicPartition
      |> Seq.choose (fun kvp -> 
        if kvp.Value = b then Some kvp.Key
        else None)
    return      
      { s with
          bootstrapBroker =
            match s.bootstrapBroker with
            | Some b' when b' = b -> None
            | b -> b
          brokersByGroup = s.brokersByGroup |> Map.removeAll groupIds
          brokersByTopicPartition = s.brokersByTopicPartition |> Map.removeAll topicPartitions
          brokersByNodeId = s.brokersByNodeId |> Map.remove b.nodeId
          version = s.version + 1 }
    }
  
  static member private removeBrokerChan (ep:EndPoint) (s:ClusterState) =    
    let nodeIds =
      s.brokerChansByNodeId
      |> Seq.choose (fun kvp ->
        let ep' = Chan.endpoint kvp.Value
        if ep' = ep then Some kvp.Key
        else None)    
    {
      s with
        brokerChansByEndPoint = s.brokerChansByEndPoint |> Map.remove ep
        brokerChansByNodeId = s.brokerChansByNodeId |> Map.removeAll nodeIds
        version = s.version + 1
    }

type internal RouteType =
  | BootstrapRoute
  | TopicRoute of TopicName[]
  | GroupRoute of GroupId
  | AllBrokersRoute
  with 
    static member ofRequest (req:RequestMessage) =
      match req with
      | RequestMessage.DescribeGroups _ -> AllBrokersRoute
      | RequestMessage.Fetch r -> TopicRoute (r.topics |> Array.map fst)
      | RequestMessage.GroupCoordinator _ -> BootstrapRoute
      | RequestMessage.Heartbeat r -> GroupRoute r.groupId
      | RequestMessage.JoinGroup r -> GroupRoute r.groupId
      | RequestMessage.LeaveGroup r -> GroupRoute r.groupId
      | RequestMessage.ListGroups _ -> AllBrokersRoute
      | RequestMessage.Metadata _ -> BootstrapRoute
      | RequestMessage.Offset _ -> BootstrapRoute
      | RequestMessage.OffsetCommit r -> GroupRoute r.consumerGroup
      | RequestMessage.OffsetFetch r -> GroupRoute r.consumerGroup
      | RequestMessage.Produce r -> TopicRoute (r.topics |> Array.map (fun x -> x.topic))
      | RequestMessage.SyncGroup r -> GroupRoute r.groupId
      | RequestMessage.ApiVersions _ -> BootstrapRoute


/// A route is a result where success is a set of request and host pairs
/// and failure is a set of request and missing route pairs.
/// A request can target multiple topics and as such, multiple brokers.
type internal RouteResult = Result<(RequestMessage * Chan)[], RouteType>

/// Routing topic/partition and groups to channels.
[<Compile(Module)>]
module internal Routing =

  /// Partitions a fetch request by topic/partition and wraps each one in a request.
  let private partitionFetchReq (state:ClusterState) (req:FetchRequest) =
    req.topics
    |> Seq.collect (fun (tn, ps) -> ps |> Array.map (fun (p, o, _, mb) -> (tn, p, o, mb)))
    |> Seq.groupBy (fun (tn, p, _, _) -> ClusterState.tryFindTopicPartitionBroker (tn, p) state |> Result.ofOptionMap (fun () -> tn))
    |> Seq.map (fun (ch,reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> t, ps |> Seq.map (fun (_, p, o, mb) -> (p, o, 0L, mb)) |> Seq.toArray)
        |> Seq.toArray      
      let req = new FetchRequest(req.replicaId, req.maxWaitTime, req.minBytes, topics, req.maxBytes, 0y)
      ch, RequestMessage.Fetch req)
    |> Seq.toArray

  /// Partitions a produce request by topic/partition.
  let private partitionProduceReq (state:ClusterState) (req:ProduceRequest) =
    req.topics
    |> Seq.collect (fun x -> x.partitions |> Array.map (fun y -> (x.topic, y.partition, y.messageSetSize, y.messageSet)))
    |> Seq.groupBy (fun (t, p, _, _) -> ClusterState.tryFindTopicPartitionBroker (t, p) state |> Result.ofOptionMap (fun () -> t))
    |> Seq.map (fun (ep,reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> ProduceRequestTopicMessageSet (t, (ps |> Seq.map (fun (_, p, mss, ms) -> ProduceRequestPartitionMessageSet (p, mss, ms)) |> Seq.toArray)))
        |> Seq.toArray
      let req = new ProduceRequest(req.requiredAcks, req.timeout, topics)
      (ep, RequestMessage.Produce req))
    |> Seq.toArray

  let private partitionOffsetReq (state:ClusterState) (req:OffsetRequest) =
    req.topics
    |> Seq.collect (fun (t, ps) -> ps |> Array.map (fun (p, tm, mo) -> (t, p, tm, mo)))
    |> Seq.groupBy (fun (t, p, _, _) -> ClusterState.tryFindTopicPartitionBroker (t, p) state |> Result.ofOptionMap (fun () -> t))
    |> Seq.map (fun (ep,reqs) ->
      let topics =
        reqs
        |> Seq.groupBy (fun (t, _, _, _) -> t)
        |> Seq.map (fun (t, ps) -> (t, (ps |> Seq.map (fun (_, p, mss, ms) -> (p, mss, ms)) |> Seq.toArray)))
        |> Seq.toArray
      let req = new OffsetRequest(req.replicaId, topics)
      ep, RequestMessage.Offset req)
    |> Seq.toArray

  let concatFetchRes (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toFetch
    |> (fun rs -> 
      let res =
        if rs.Length = 0 then 
          new FetchResponse (0, [||])
        else
          let tt = rs |> Seq.map (fun r -> r.throttleTime) |> Seq.max
          new FetchResponse(tt, rs |> Array.collect (fun r -> r.topics))
      ResponseMessage.FetchResponse res)

  let concatListGroupsRes (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toListGroups
    |> (fun rs -> 
      let res =
        if rs.Length = 0 then 
          new ListGroupsResponse (ErrorCode.NoError, [||], 0)
        else
          let ec = 
            rs 
            |> Seq.tryPick (fun r -> if r.errorCode <> ErrorCode.NoError then Some r.errorCode else None)
            |> Option.getOr ErrorCode.NoError
          new ListGroupsResponse (ec, rs |> Array.collect (fun r -> r.groups), 0)
      ResponseMessage.ListGroupsResponse res)

  let concatOffsetResponses (rs:ResponseMessage[]) =
    rs
    |> Array.map ResponseMessage.toOffset
    |> (fun rs -> new OffsetResponse(rs |> Array.collect (fun r -> r.topics)) |> ResponseMessage.OffsetResponse)
  
  let route (state:ClusterState) : RequestMessage -> Result<(RequestMessage * Broker)[], RouteType> =

    let bootstrapRoute (req:RequestMessage) =
      match ClusterState.tryFindBootstrapBroker state with
      | Some b -> Success [| req, b |]
      | None -> Failure (RouteType.BootstrapRoute)

    let topicRoute (xs:(Result<Broker, TopicName> * RequestMessage)[]) =
      xs
      |> Result.traverse (fun (routeRes,req) ->
        match routeRes with
        | Success ch -> Success (req,ch)
        | Failure tn -> Failure (RouteType.TopicRoute [|tn|]))

    let groupRoute req gid =
      match ClusterState.tryFindGroupCoordinatorBroker gid state with
      | Some ch -> Success [| req,ch |]
      | None -> Failure (RouteType.GroupRoute gid)

    let allBrokersRoute (req:RequestMessage) =
      if state.brokersByNodeId.Count = 0 then Failure RouteType.AllBrokersRoute else      
      state.brokersByNodeId
      |> Seq.map (fun kvp -> req,kvp.Value)
      |> Seq.toArray
      |> Success

    fun (req:RequestMessage) ->
      match req with
      | Metadata _ -> bootstrapRoute req
      | GroupCoordinator _ -> bootstrapRoute req
      | DescribeGroups _ -> allBrokersRoute req
      | ListGroups _req -> allBrokersRoute req
      | ApiVersions _req -> bootstrapRoute req
      
      | Fetch req -> req |> partitionFetchReq state |> topicRoute
      | Offset req -> req |> partitionOffsetReq state |> topicRoute

      // TODO: unsupported?
      | Produce req -> req |> partitionProduceReq state |> topicRoute
      
      | OffsetCommit r -> groupRoute req r.consumerGroup
      | OffsetFetch r -> groupRoute req r.consumerGroup
      | JoinGroup r -> groupRoute req r.groupId
      | SyncGroup r -> groupRoute req r.groupId
      | Heartbeat r -> groupRoute req r.groupId
      | LeaveGroup r -> groupRoute req r.groupId

 


/// Indicates an action to take in response to a request error.
type private RetryAction =
  
  // refresh routes
  | RefreshMetadataAndRetry of topics:TopicName[]

  // wait
  | WaitAndRetry

  // escalate
  | Escalate
  | PassThru

  with

    static member errorRetryAction (ec:ErrorCode) (topicNames:TopicName[]) =
      match ec with
      | ErrorCode.NoError -> None

      | ErrorCode.NotCoordinatorForGroupCode | ErrorCode.GroupCoordinatorNotAvailableCode when topicNames.Length > 0 -> 
        Some (RefreshMetadataAndRetry topicNames)
      
      | ErrorCode.LeaderNotAvailable | ErrorCode.RequestTimedOut | ErrorCode.GroupLoadInProgressCode | ErrorCode.GroupCoordinatorNotAvailableCode
      | ErrorCode.NotEnoughReplicasAfterAppendCode | ErrorCode.NotEnoughReplicasCode ->
        Some (RetryAction.WaitAndRetry)
        
      | ErrorCode.NotCoordinatorForGroupCode | ErrorCode.IllegalGenerationCode | ErrorCode.OffsetOutOfRange | ErrorCode.UnknownMemberIdCode -> 
        Some (RetryAction.PassThru)
      
      | ErrorCode.UnknownTopicOrPartition ->
        Some (RetryAction.Escalate)
        //Some (RetryAction.RefreshMetadataAndRetry)

      | ErrorCode.InvalidMessage ->
        Some (RetryAction.Escalate)
      | _ ->
        Some (RetryAction.Escalate)

    static member tryFindError (res:ResponseMessage) =
      match res with
      | ResponseMessage.MetadataResponse r ->
        r.topicMetadata
        |> Seq.tryPick (fun x ->
          match x.topicErrorCode with
          | ErrorCode.UnknownTopicOrPartition -> 
            Some (x.topicErrorCode,RetryAction.RefreshMetadataAndRetry [|x.topicName|])
          | _ ->
            RetryAction.errorRetryAction x.topicErrorCode [|x.topicName|]
            |> Option.map (fun action -> x.topicErrorCode,action))

      | ResponseMessage.OffsetResponse r ->
        r.topics
        |> Seq.tryPick (fun (tn,ps) -> 
          ps
          |> Seq.tryPick (fun x -> 
            match x.errorCode with
            | ErrorCode.NoError -> None
            | ErrorCode.UnknownTopicOrPartition | ErrorCode.NotLeaderForPartition -> 
              Some (x.errorCode, RetryAction.RefreshMetadataAndRetry [|tn|])
            | _ -> Some (x.errorCode, RetryAction.Escalate)))

      | ResponseMessage.FetchResponse r ->
        r.topics 
        |> Seq.tryPick (fun (topicName,partitionMetadata) -> 
          partitionMetadata 
          |> Seq.tryPick (fun p -> 
            let ec = p.errorCode
            match ec with
            | ErrorCode.NoError -> None
            | ErrorCode.NotLeaderForPartition | ErrorCode.UnknownTopicOrPartition -> 
              Some (ec, RetryAction.RefreshMetadataAndRetry [|topicName|])
            | ec ->
              RetryAction.errorRetryAction ec [|topicName|]
              |> Option.map (fun action -> ec, action)))
      
      | ResponseMessage.GroupCoordinatorResponse r ->
        RetryAction.errorRetryAction r.errorCode  [||]
        |> Option.map (fun action -> r.errorCode,action)

      | ResponseMessage.HeartbeatResponse r ->
        match r.errorCode with 
        | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
          Some (r.errorCode,RetryAction.PassThru)
        | _ ->
          RetryAction.errorRetryAction r.errorCode [||]
          |> Option.map (fun action -> r.errorCode,action)

      | ResponseMessage.OffsetFetchResponse r -> 
        match r.errorCode with
        | ec when ec <> ErrorCode.NoError -> Some (ec, RetryAction.PassThru)
        | _ ->
            r.topics
            |> Seq.tryPick (fun (_t,ps) ->
              ps
              |> Seq.tryPick (fun (_p,_o,_md,ec) -> 
                match ec with
                | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
                  Some (ec,RetryAction.PassThru)
                | _ ->
                  RetryAction.errorRetryAction ec [|_t|]
                  |> Option.map (fun action -> ec,action)))

      | ResponseMessage.OffsetCommitResponse r ->
        r.topics
        |> Seq.tryPick (fun (_tn,ps) ->
          ps
          |> Seq.tryPick (fun (_p,ec) -> 
            match ec with
            | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
              Some (ec,RetryAction.PassThru)
            | _ ->
              RetryAction.errorRetryAction ec [|_tn|]
              |> Option.map (fun action -> ec,action)))
                        
      | ResponseMessage.JoinGroupResponse r ->
        match r.errorCode with
        | ErrorCode.UnknownMemberIdCode ->
          Some (r.errorCode,RetryAction.PassThru)
        | _ ->
          RetryAction.errorRetryAction r.errorCode [||]
          |> Option.map (fun action -> r.errorCode,action)

      | ResponseMessage.SyncGroupResponse r ->
        match r.errorCode with 
        | ErrorCode.UnknownMemberIdCode | ErrorCode.IllegalGenerationCode | ErrorCode.RebalanceInProgressCode ->
          Some (r.errorCode,RetryAction.PassThru)
        | _ ->
          RetryAction.errorRetryAction r.errorCode [||]
          |> Option.map (fun action -> r.errorCode,action)
      
      | ResponseMessage.LeaveGroupResponse r ->
        RetryAction.errorRetryAction r.errorCode [||]
        |> Option.map (fun action -> r.errorCode,action)

      | ResponseMessage.DescribeGroupsResponse r ->
        r.groups
        |> Seq.tryPick (fun (ec,_,_,_,_,_) -> 
          RetryAction.errorRetryAction ec [||]
          |> Option.map (fun action -> ec,action))

      | ResponseMessage.ListGroupsResponse r ->
        RetryAction.errorRetryAction r.errorCode [||]
        |> Option.map (fun action -> r.errorCode,action)
    
      | ResponseMessage.ProduceResponse _ ->
        None

      | ResponseMessage.ApiVersionsResponse r ->
        RetryAction.errorRetryAction r.errorCode [||]
        |> Option.map (fun a -> r.errorCode,a)


/// Operations for parsing Kafka URIs.
module KafkaUri =

  open System
  open System.Text.RegularExpressions

  let [<Literal>] DefaultPortKafka = 9092
  let [<Literal>] UriSchemeKafka = "kafka"
  let private KafkaBrokerUriRegex = Regex("^(?<scheme>(kafka|tcp)://)?(?<host>[-._\w]+)(:(?<port>[\d]+))?", RegexOptions.Compiled)

  /// Parses a string into a Kafka Uri.
  let parse (host:string) =
    let m = KafkaBrokerUriRegex.Match host
    if not m.Success then invalidArg "host" (sprintf "invalid host string '%s'" host)
    else
      let host = m.Groups.["host"].Value
      let port = 
        let g = m.Groups.["port"]
        if g.Success then Int32.Parse g.Value
        else DefaultPortKafka
      let ub = UriBuilder(UriSchemeKafka, host, port)
      ub.Uri


/// Kafka connection configuration.
type KafkaConfig = {
  
  /// The Kafka server version.
  version : Version

  /// Indicates whether API versions are automatically determined based on
  /// the ApiVersionsResponse.
  autoApiVersions : bool

  /// The bootstrap brokers to attempt connection to.
  bootstrapServers : Uri list
  
  /// The retry policy for connecting to bootstrap brokers.
  bootstrapConnectRetryPolicy : RetryPolicy

  /// The retry policy for broker requests.
  requestRetryPolicy : RetryPolicy

  /// A logical application name to pass to the server along with requests.
  /// The purpose of this is to be able to track the source of requests beyond just ip/port.
  clientId : ClientId
  
  /// TCP connection configuration.
  tcpConfig : ChanConfig 

  /// A unique connection id.
  connId : string

} with

  /// The default Kafka server version = 0.10.1.
  static member DefaultVersion = Versions.V_0_10_1

  /// The default setting for supporting auto API versions = true.
  static member DefaultAutoApiVersions = true

  /// The default broker channel configuration.
  static member DefaultChanConfig = ChanConfig.create ()

  /// The default client id = "".
  static member DefaultClientId = ""

  /// The default bootstrap broker connection retry policy = RetryPolicy.constantBoundedMs 2000 3.
  static member DefaultBootstrapConnectRetryPolicy = RetryPolicy.constantBoundedMs 1000 3

  /// The default request retry policy = RetryPolicy.constantBoundedMs 1000 20.
  static member DefaultRequestRetryPolicy = RetryPolicy.constantBoundedMs 1000 20

  ///// The default request retry policy = RetryPolicy.expRandLimitBoundedMs 100 1.5 0.2 1000 2.
  //static member DefaultRequestRetryPolicy = RetryPolicy.expRandLimitBoundedMs 100 1.5 0.2 1000 2

  /// Creates a Kafka configuration object.
  static member create (bootstrapServers:Uri list, ?clientId:ClientId, ?tcpConfig, ?bootstrapConnectRetryPolicy, ?requestRetryPolicy, 
                        ?version, ?autoApiVersions) =
    
    // auto-api doesn't exist on versions prior to 0.10.0
    let autoApiVersions =
      match version with
      | Some v when v < Versions.V_0_10_0 -> Some false
      | _ -> autoApiVersions

    { version = defaultArg version KafkaConfig.DefaultVersion
      autoApiVersions = defaultArg autoApiVersions KafkaConfig.DefaultAutoApiVersions
      bootstrapServers = bootstrapServers
      bootstrapConnectRetryPolicy = defaultArg bootstrapConnectRetryPolicy KafkaConfig.DefaultBootstrapConnectRetryPolicy
      requestRetryPolicy = defaultArg requestRetryPolicy KafkaConfig.DefaultRequestRetryPolicy
      clientId = match clientId with Some clientId -> clientId | None -> KafkaConfig.DefaultClientId
      tcpConfig = defaultArg tcpConfig KafkaConfig.DefaultChanConfig
      connId = Guid.NewGuid().ToString("N") }


/// An exception used to wrap failures which are to be escalated.
type EscalationException (errorCode:ErrorCode, req:RequestMessage, res:ResponseMessage, msg:string) =
  inherit Exception (sprintf "Kafka exception|error_code=%i request=%s response=%s message=%s" errorCode (RequestMessage.Print req) (ResponseMessage.Print res) msg)

/// A connection to a Kafka cluster.
/// This is a stateful object which maintains request/reply sessions with brokers.
/// It acts as a context for API operations, providing filtering and fault tolerance.
[<NoEquality;NoComparison;AutoSerializable(false)>]
type KafkaConn internal (cfg:KafkaConfig) =

  static let Log = Log.create "Kafunk.Conn"
  
  do Log.info "created_conn|api_version=%O auto_api_versions=%b client_version=%O client_id=%s conn_id=%s" 
        cfg.version cfg.autoApiVersions (Assembly.executingAssemblyVersion ()) cfg.clientId cfg.connId

  do if String.IsNullOrEmpty cfg.clientId then Log.warn "client_id_unspecified"

  let apiVersion = ref (Versions.byVersion cfg.version)
  let stateCell : MVar<ClusterState> = MVar.createFull (ClusterState.Zero)
  let cts = new CancellationTokenSource()

  // NB: The presence of the critical boolean flag is unfortunate but required to
  // address reentrancy issues. Failures are recovered inside of a critical region
  // to prevent a thundering herd problem where many concurrent requests are failing
  // and attempting to reover. This works well, except in cases where the recovery may
  // itself need recovery (such as when a metadata refresh requires a bootstrap rediscovery).
  // Another shortcoming is that the Producer has its own recovery semantics atop a Chan, while
  // the Consumer does not. The Producer needs to handle recovery explicitly because it must
  // reconfigure its broker queues. The problem is that the Producer foregoes the routing
  // capabilities provided by the underlying connection.

  /// Connects to the broker at the specified endpoint.
  let rec connBrokerEndPoint (connState:ClusterState) (ep:EndPoint) : Async<Result<Chan, exn>> = async {
    match connState |> ClusterState.tryFindBrokerChanByEndPoint ep with
    | Some ch ->
      try
        do! Chan.ensureOpen ch
        return Success ch
      with ex ->
        return Failure ex
    | _ ->
      try
        let! ch = Chan.connect (cfg.connId, !apiVersion, cfg.tcpConfig, cfg.clientId) ep
        return Success ch
      with ex ->
        return Failure ex }

  /// Connects to the broker at the specified host, attemping all discovered IPs.
  and connBroker (connState:ClusterState) (b:Broker) : Async<Result<ClusterState, exn>> = async {
    let! ips = async {
      match IPAddress.tryParse b.host with
      | Some ip ->
        return [|ip|]
      | None ->
        let! ips = Dns.IPv4.getAllAsync b.host
        Log.info "discovered_dns|host=%s ips=[%s] conn_id=%s" b.host (Printers.stringsCsv ips) cfg.connId
        return ips }
    return!
      ips
      |> Seq.map (fun ip -> EndPoint.ofIPAddressAndPort (ip, b.port))
      |> AsyncSeq.ofSeq
      |> AsyncSeq.traverseAsyncResult Exn.monoid (fun ep -> async {
        let! connRes = connBrokerEndPoint connState ep
        match connRes with
        | Success ch ->
          //Log.info "connected_to_broker|broker=%O" (Broker.endpoint b)
          return connState |> ClusterState.addBrokerChan (b, ch) |> Success
        | Failure ex ->
          //Log.error "failed_to_connect_to_broker|broker=%O error=\"%O\"" (Broker.endpoint b) ex
          return Failure ex }) }

  /// Connects to the specified broker and stores the connection in the cluster state.
  and connBrokerAndApply (callingState:ClusterState) (b:Broker) : Async<Result<ClusterState, exn>> =
    stateCell
    |> MVar.updateStateAsync (fun currentState -> async {
      if ClusterState.containsBrokerChan currentState b.nodeId then 
        return currentState, Success currentState
      else
        let! ch = connBroker currentState b
        match ch with
        | Success state' -> 
          return state', Success state'
        | Failure ex ->
          return currentState, Failure ex })
  
  /// Removes a broker from the cluster state view.
  and removeBrokerAndApply (b:Broker) (callingState:ClusterState) =
    stateCell 
    |> MVar.updateAsync (fun currentState -> async {
      if (currentState.version = callingState.version) then
        Log.warn "removing_broker|version=%i node_id=%i ep=%O conn_id=%s" 
          currentState.version b.nodeId (Broker.endpoint b) cfg.connId
        return! currentState |> ClusterState.removeBroker b
      else
        Log.trace "skipping_remove_broker|current_version=%i caller_version=%i node_id=%i ep=%O" 
          currentState.version callingState.version b.nodeId (Broker.endpoint b)
        return currentState })

  /// Connects to the first available bootstrap broker and adds the connection to the cluster state.
  and bootstrap =
    let connect (rs:RetryState) (callingState:ClusterState) = async { 
      Log.info "connecting_to_bootstrap_brokers|brokers=%A attempt=%i conn_id=%s" cfg.bootstrapServers rs.attempt cfg.connId
      return!
        cfg.bootstrapServers
        |> AsyncSeq.ofSeq
        |> AsyncSeq.traverseAsyncResult Exn.monoid (fun uri -> async {
          // Log.info "connecting_to_bootstrap_broker|conn_id=%s broker=%O" cfg.connId uri
          // NB: broker with negative id so as to not overlap with brokers where id is known
          let b = Broker(-2, uri.Host, uri.Port) 
          let! state' = connBroker callingState b
          match state' with
          | Success state' ->            
            return state' |> ClusterState.updateBootstrapBroker b |> Success
          | Failure ex ->            
            return Failure ex }) }
    connect
    |> Faults.AsyncFunc.retryStateResultThrowList 
        (fun errs ->
          let exnInner = Exn.ofSeq errs
          Log.error "failed_to_connect_bootstrap_brokers|brokers=%A error=\"%O\"" cfg.bootstrapServers exnInner
          exn("Failed to connect to a bootstrap broker.", exnInner))
        cfg.bootstrapConnectRetryPolicy
    
  /// Connects to the first available broker in the bootstrap list and returns the 
  /// initial routing table.
  and getAndApplyBootstrap =
    stateCell |> MVar.updateAsync bootstrap

  and refreshBootstrap critical state =
    if critical then bootstrap state
    else getAndApplyBootstrap

  /// Fetches metadata and returns an updated connection state.
  and metadata (state:ClusterState) (rs:RetryState) (topics:TopicName[]) = async {
    
    let send =
      routeToBrokerWithRecovery true rs state
      |> AsyncFunc.dimap RequestMessage.Metadata ResponseMessage.toMetadata
    
    let! metadata = send (MetadataRequest(topics))
    Log.info "received_cluster_metadata|%s" (MetadataResponse.Print metadata)

    /// TODO: spin on missing leader?
    for tmd in metadata.topicMetadata do
      for pmd in tmd.partitionMetadata do
        if pmd.leader = -1 then
          Log.error "leaderless_partition_detected|topic=%s partition=%i error_code=%i" tmd.topicName pmd.partitionId pmd.partitionErrorCode
      
    let topicNodes =
      metadata.topicMetadata 
      |> Seq.collect (fun tmd -> 
        tmd.partitionMetadata 
        |> Seq.map (fun pmd -> tmd.topicName, pmd.partitionId, pmd.leader))
      |> Seq.toArray

    return state |> ClusterState.updateMetadata (metadata.brokers, topicNodes) }

  /// Fetches and applies metadata to the current connection.
  and getAndApplyMetadata (requireMatchingCaller:bool) (callerState:ClusterState) (rs:RetryState) (topics:TopicName[]) =
    stateCell
    |> MVar.updateAsync (fun (currentState:ClusterState) -> async {
      if requireMatchingCaller 
        && currentState.version > callerState.version 
        && ClusterState.containsTopicMetadata topics currentState then 
        Log.trace "skipping_metadata_update|current_version=%i caller_version=%i" currentState.version callerState.version
        return currentState 
      else
        return! metadata currentState rs topics })

  /// Refreshes metadata for existing topics.
  and refreshMetadata (critical:bool) (callerState:ClusterState) =
    let topics = 
      ClusterState.topicPartitions callerState
      |> Seq.map (fun kvp -> kvp.Key)
      |> Seq.toArray
    refreshMetadataFor critical callerState topics

  and refreshMetadataFor (critical:bool) (callerState:ClusterState) topics =
    Log.info "refreshing_metadata|topics=%A version=%i bootstrap_broker=%A conn_id=%s" 
      topics callerState.version (callerState.bootstrapBroker |> Option.map (Broker.endpoint)) cfg.connId
    if critical then metadata callerState RetryState.init topics
    else getAndApplyMetadata true callerState RetryState.init topics

  /// Fetches group coordinator metadata.
  and groupCoordinator (state:ClusterState) (groupId:GroupId) = async {
    let send = 
      routeToBrokerWithRecovery true RetryState.init state
      |> AsyncFunc.dimap RequestMessage.GroupCoordinator (ResponseMessage.toGroupCoordinator)
    let! res = send (GroupCoordinatorRequest(groupId))
    Log.info "received_group_coordinator|group_id=%s conn_id=%s %s" 
      groupId cfg.connId (GroupCoordinatorResponse.Print res)
    return 
      state 
      |> ClusterState.updateGroupCoordinator (Broker(res.coordinatorId, res.coordinatorHost, res.coordinatorPort), groupId) }

  /// Fetches the group coordinator and applies the state to the current connection.
  and getAndApplyGroupCoordinator (callerState:ClusterState) (groupId:GroupId) =
    stateCell 
    |> MVar.updateAsync (fun (currentState:ClusterState) -> async {
      //if currentState.version > callerState.version 
      //  && ClusterState.containsGroupCoordinator groupId currentState then 
      // TODO: review
      if false then
        Log.trace "skipping_group_coordinator_update|current_version=%i caller_version=%i group_id=%s" currentState.version callerState.version groupId
        return currentState 
      else
        let! state' = groupCoordinator currentState groupId
        return state' })

  and refreshGroupCoordinator critical (callerState:ClusterState) (groupId:GroupId) =
    if critical then groupCoordinator callerState groupId
    else getAndApplyGroupCoordinator callerState groupId

  /// Gets a channel for the specified broker.
  and getBrokerChan (critical:bool) (state:ClusterState) (b:Broker) = async {
    match ClusterState.tryFindBrokerChanById b.nodeId state with
    | Some ch ->
      // TODO: if channel is faulted, return error and force metadata refresh
      return Success ch
    | None ->
      Log.trace "broker_chan_missing|node_id=%i ep=%O version=%i" b.nodeId (Broker.endpoint b) state.version
      let! brokerConn = 
        if critical then connBroker state b
        else connBrokerAndApply state b
      match brokerConn with
      | Success state' ->
        return! getBrokerChan critical state' b
      | Failure ex ->
        return Failure [ChanError.ChanFailure ex] }

  /// Sends a request to a specific broker.
  and sendToBroker (critical:bool) (state:ClusterState) (b:Broker) (req:RequestMessage) : Async<ChanResult> = async {
    let! ch = getBrokerChan critical state b
    match ch with
    | Success ch ->
      //Log.info "sending_to_broker|node_id=%i ep=%O req=%s" b.nodeId (Broker.endpoint b) (RequestMessage.Print req)
      try
        let! chanRes = Chan.send ch req
        match chanRes with
        | Success res ->
          //Log.info "received_response|node_id=%i ep=%O req=%s res=%s" 
          //  b.nodeId (Broker.endpoint b) (RequestMessage.Print req) (ResponseMessage.Print res)
          return Success res
        | Failure errs ->
          let! _state =
            if critical then ClusterState.removeBroker b state
            else removeBrokerAndApply b state
          return Failure errs
       with ex ->
        match Exn.tryFindByTypeT3<ResponseDecodeException, FramingException, OutOfMemoryException> ex with
        | Some ex ->
          return raise ex        
        | None ->
          let! _state =
            if critical then ClusterState.removeBroker b state
            else removeBrokerAndApply b state
          return Failure [ChanError.ChanFailure ex]
    | Failure errs ->
      let! _state =
        if critical then ClusterState.removeBroker b state
        else removeBrokerAndApply b state
      return Failure errs }

  /// Sends a request to a specific broker and handles failures.
  and sendToBrokerWithRecovery (critical:bool) (rs:RetryState) (state:ClusterState) (b:Broker) (req:RequestMessage) = async {
    return!
      sendToBroker critical state b req
      |> Async.bind (fun chanRes -> async {
        match chanRes with
        | Success res ->
          match RetryAction.tryFindError res with
          | None -> 
            return res
          | Some (errorCode,action) ->
            Log.warn "channel_response_errored|endpoint=%O error_code=%i retry_action=%A attempt=%i req=%s res=%s conn_id=%s" 
              (Broker.endpoint b) errorCode action rs.attempt (RequestMessage.Print req) (ResponseMessage.Print res) cfg.connId
            match action with
            | RetryAction.PassThru ->
              return res
            | RetryAction.Escalate ->
              return raise (EscalationException (errorCode,req,res,(sprintf "endpoint=%O" (Broker.endpoint b))))
            | RetryAction.RefreshMetadataAndRetry topics ->              
              let! rs' = RetryPolicy.awaitNextState cfg.requestRetryPolicy rs
              match rs' with
              | Some rs' ->
                let! state' = 
                  if critical then metadata state rs' topics
                  else getAndApplyMetadata true state rs' topics
                return! routeToBrokerWithRecovery critical rs' state' req
              | None ->
                return failwithf "request_failure|attempt=%i request=%s response=%s" 
                  rs.attempt (RequestMessage.Print req) (ResponseMessage.Print res)
            | RetryAction.WaitAndRetry ->
              let! rs' = RetryPolicy.awaitNextState cfg.requestRetryPolicy rs
              match rs' with
              | Some rs ->
                return! routeToBrokerWithRecovery critical rs state req
              | None ->
                return failwithf "request_failure|attempt=%i request=%s response=%s" 
                  rs.attempt (RequestMessage.Print req) (ResponseMessage.Print res)
        | Failure chanErr ->
          let! rs' = RetryPolicy.awaitNextState cfg.requestRetryPolicy rs
          match rs' with
          | Some rs ->
            let! state' = recoverBrokerChanRequestError critical state (b, req, chanErr)
            return! routeToBrokerWithRecovery critical rs state' req 
          | None ->
            let errMsg = sprintf "broker_chan_retry_limit_reached|conn_id=%s attempt=%i ep=%O errors=[%s] req=%s" 
                          cfg.connId rs.attempt (Broker.endpoint b) (ChanError.printErrors chanErr) (RequestMessage.Print req)
            Log.error "%s" errMsg
            return failwith errMsg  }) }

  /// Sends the request based on discovered routes and handles failures.
  and routeToBrokerWithRecovery (critical:bool) (rs:RetryState) (state:ClusterState) (req:RequestMessage) = async {
    match Routing.route state req with
    | Success routes ->
      // TODO: redesign to be cooperative with the caller
      let scatterGather (gather:ResponseMessage[] -> ResponseMessage) = async {
        if routes.Length = 1 then
          let req,ch = routes.[0]
          return! sendToBrokerWithRecovery critical rs state ch req
        else
          return!
            routes
            |> Seq.map (fun (req,ch) -> sendToBrokerWithRecovery critical rs state ch req)
            |> Async.Parallel
            |> Async.map gather } 
      match req with
      | RequestMessage.Offset _ -> 
        return! scatterGather Routing.concatOffsetResponses
      | RequestMessage.Fetch _ ->
        return! scatterGather Routing.concatFetchRes
      | RequestMessage.ListGroups _ ->
        return! scatterGather Routing.concatListGroupsRes
      | _ ->
        let req,b = routes.[0]
        return! sendToBrokerWithRecovery critical rs state b req

    | Failure rt ->
      Log.trace "missing_route|route_type=%A request=%s" rt (RequestMessage.Print req)
      // TODO: should this delay on initial call?
      let! rs' = RetryPolicy.awaitNextState cfg.requestRetryPolicy rs
      match rs' with
      | Some rs -> 
        let! state' = async {
          match rt with
          | RouteType.BootstrapRoute ->
            return! refreshBootstrap critical state
          | RouteType.GroupRoute gid ->
            return! refreshGroupCoordinator critical state gid
          | RouteType.TopicRoute tns ->
            return! refreshMetadataFor critical state tns
          | RouteType.AllBrokersRoute ->
            return! refreshMetadataFor critical state [||] }
        return! routeToBrokerWithRecovery critical rs state' req
      | None ->
        return failwithf "missing_route|attempts=%i route_type=%A" rs.attempt rt }

  /// Handles a failure to communicate with a broker.
  and recoverBrokerChanRequestError (critical:bool) (state:ClusterState) (b:Broker, req:RequestMessage, chanErrs:ChanError list) = async {
    Log.warn "handling_broker_chan_error|node_id=%i endpoint=%O req=%s critical=%b errors=[%s] conn_id=%s" 
      b.nodeId (Broker.endpoint b) (RequestMessage.Print req) critical (ChanError.printErrors chanErrs) cfg.connId
    match RouteType.ofRequest req with
    | RouteType.BootstrapRoute ->
      return! refreshBootstrap critical state
    | RouteType.GroupRoute groupId ->
      return! refreshGroupCoordinator critical state groupId
    | RouteType.TopicRoute _ | RouteType.AllBrokersRoute ->
      return! refreshMetadata critical state }

  /// Gets the cancellation token triggered when the connection is closed.
  member internal __.CancellationToken = cts.Token

  /// Gets the configuration for the connection.
  member __.Config = cfg

  member private __.GetState () =
    let state = MVar.getFastUnsafe stateCell
    if state.IsNone then
      invalidOp "Connection state unavailable; must not be connected."
    else
      state.Value

  member internal __.SendToBroker (b:Broker, req:RequestMessage) = async {
    let state = __.GetState ()
    let! res = sendToBroker false state b req
    return res }

  member internal __.Send (req:RequestMessage) : Async<ResponseMessage> = async {
    let state = __.GetState ()
    return! routeToBrokerWithRecovery false RetryState.init state req }
  
  /// Connects to a broker from the bootstrap list.
  member internal __.Connect () = async {
    let! _ = getAndApplyBootstrap
    if cfg.autoApiVersions then      
      let! res = __.Send (RequestMessage.ApiVersions (ApiVersionsRequest()))
      let res = res |> ResponseMessage.toApiVersions
      Log.info "discovered_api_versions|conn_id=%s %s" cfg.connId (ApiVersionsResponse.Print res)
      apiVersion := Versions.byApiVersionResponse res
    return () }

  member internal __.ApiVersion (apiKey:ApiKey) : ApiVersion =
    !apiVersion apiKey

  member internal __.GetGroupCoordinator (groupId:GroupId) = async {
    let! state = MVar.get stateCell
    let! state' = getAndApplyGroupCoordinator state groupId
    let broker = ClusterState.tryFindGroupCoordinatorBroker groupId state'
    return broker |> Option.get }

  member internal __.GetMetadataState (topics:TopicName[]) = async {
    let! state = MVar.get stateCell
    return! getAndApplyMetadata false state RetryState.init topics }

  member internal __.GetMetadata (topics:TopicName[]) = async {
    let! state' = __.GetMetadataState topics
    return state' |> ClusterState.topicPartitions |> Map.onlyKeys topics }

  member __.Close () =
    Log.info "closing_connection|conn_id=%s" cfg.connId
    cts.Cancel()
    (stateCell :> IDisposable).Dispose()
    
  interface IDisposable with
    member __.Dispose () =
      __.Close ()


/// Kafka API.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Kafka =

  let private Log = Log.create "Kafunk"
  
  /// Connects to a Kafka cluster.
  let connAsync (cfg:KafkaConfig) = async {
    let conn = new KafkaConn(cfg)
    do! conn.Connect ()
    return conn }

  /// Connects to a Kafka cluster.
  let conn cfg =
    connAsync cfg |> Async.RunSynchronously

  /// Connects to a Kafka cluster given a default configuration.
  let connHostAsync (host:string) =
    let uri = KafkaUri.parse host
    let cfg = KafkaConfig.create [uri]
    connAsync cfg

  /// Connects to a Kafka cluster given a default configuration.
  let connHost host =
    connHostAsync host |> Async.RunSynchronously

  let metadata (c:KafkaConn) : MetadataRequest -> Async<MetadataResponse> =
    AsyncFunc.dimap RequestMessage.Metadata ResponseMessage.toMetadata c.Send

  let fetch (c:KafkaConn) : FetchRequest -> Async<FetchResponse> =
    AsyncFunc.dimap RequestMessage.Fetch ResponseMessage.toFetch c.Send

  let produce (c:KafkaConn) : ProduceRequest -> Async<ProduceResponse> =
    AsyncFunc.dimap RequestMessage.Produce ResponseMessage.toProduce c.Send

  let offset (c:KafkaConn) : OffsetRequest -> Async<OffsetResponse> =
    AsyncFunc.dimap RequestMessage.Offset ResponseMessage.toOffset c.Send

  let groupCoordinator (c:KafkaConn) : GroupCoordinatorRequest -> Async<GroupCoordinatorResponse> =
    AsyncFunc.dimap RequestMessage.GroupCoordinator ResponseMessage.toGroupCoordinator c.Send

  let offsetCommit (c:KafkaConn) : OffsetCommitRequest -> Async<OffsetCommitResponse> =
    AsyncFunc.dimap RequestMessage.OffsetCommit ResponseMessage.toOffsetCommit c.Send

  let offsetFetch (c:KafkaConn) : OffsetFetchRequest -> Async<OffsetFetchResponse> =
    AsyncFunc.dimap RequestMessage.OffsetFetch ResponseMessage.toOffsetFetch c.Send

  let joinGroup (c:KafkaConn) : JoinGroupRequest -> Async<JoinGroupResponse> =
    AsyncFunc.dimap RequestMessage.JoinGroup ResponseMessage.toJoinGroup c.Send

  let syncGroup (c:KafkaConn) : SyncGroupRequest -> Async<SyncGroupResponse> =
    AsyncFunc.dimap RequestMessage.SyncGroup ResponseMessage.toSyncGroup c.Send

  let heartbeat (c:KafkaConn) : HeartbeatRequest -> Async<HeartbeatResponse> =
    AsyncFunc.dimap RequestMessage.Heartbeat ResponseMessage.toHeartbeat c.Send

  let leaveGroup (c:KafkaConn) : LeaveGroupRequest -> Async<LeaveGroupResponse> =
    AsyncFunc.dimap RequestMessage.LeaveGroup ResponseMessage.toLeaveGroup c.Send

  let listGroups (c:KafkaConn) : ListGroupsRequest -> Async<ListGroupsResponse> =
    AsyncFunc.dimap RequestMessage.ListGroups ResponseMessage.toListGroups c.Send

  let describeGroups (c:KafkaConn) : DescribeGroupsRequest -> Async<DescribeGroupsResponse> =
    AsyncFunc.dimap RequestMessage.DescribeGroups ResponseMessage.toDescribeGroups c.Send

  let apiVersions (c:KafkaConn) : Async<ApiVersionsResponse> =
    c.Send (RequestMessage.ApiVersions (ApiVersionsRequest())) |> Async.map ResponseMessage.toApiVersions
  

  