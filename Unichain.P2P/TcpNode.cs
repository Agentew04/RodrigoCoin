﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Unichain.P2P.Packets;

namespace Unichain.P2P;
public abstract class TcpNode {

    #region Variables

    /// <summary>
    /// The unique identifier of this node.
    /// </summary>
    protected readonly Guid nodeId;

    /// <summary>
    /// The port that this node is listening on. Identification
    /// should not occur with address:port, instead with <see cref="nodeId"/>
    /// </summary>
    protected readonly int port;

    /// <inheritdoc cref="port"/>
    public int Port => port;

    /// <summary>
    /// A list with all the peers that this node is connected/knows about
    /// </summary>
    protected List<Address> peers = [];

    /// <summary>
    /// Listener to receive messages from other nodes
    /// </summary>
    private readonly TcpListener tcpListener;

    /// <summary>
    /// Logger to log messages to the console
    /// </summary>
    private readonly Logger logger;

    /// <summary>
    /// The internal thread that will run the node.
    /// </summary>
    private readonly Thread thread;

    /// <summary>
    /// Source for the cancellation token
    /// </summary>
    private readonly CancellationTokenSource cancellationTokenSource = new();

    /// <summary>
    /// A list to record recently sent broadcast messages
    /// </summary>
    private readonly FixedList<string> lastPropagations = new(10);

    #endregion

    /// <summary>
    /// Initializes variables for the <see cref="TcpNode"/>
    /// </summary>
    /// <param name="port">The port that this node will listen</param>
    protected TcpNode(int port) {
        nodeId = Guid.NewGuid();
        tcpListener = new(new IPEndPoint(IPAddress.Any, port));
        this.port = port;
        logger = new Logger(nameof(TcpNode) + " " + port.ToString());
        thread = new(ThreadMain);
    }


    #region Public Methods

    /// <summary>
    /// Starts the internal thread of this node.
    /// </summary>
    /// <param name="bootnode">The bootnode to get peers. If this is null, it will be
    /// a new bootnode. Effectively creating a new network</param>
    /// <returns></returns>
    public async Task Start(Address? bootnode) {
        if (bootnode is not null) {
            await FetchPeers(bootnode);
        }
        logger.Log($"Starting node with {peers.Count} peers...");
        thread.Start();
    }

    /// <summary>
    /// Asks the node to stop acception connections and sending messages
    /// </summary>
    /// <returns></returns>
    public async Task Stop() {
        cancellationTokenSource.Cancel();
        try { 
            await Task.Run(thread.Join);
        }catch(ThreadStateException e) {
            logger.LogError($"Failed to stop node! {e.Message}");
        }catch(ThreadInterruptedException e) {
            logger.LogError($"Failed to stop node! {e.Message}");
        }
    }

    #endregion

    #region Protected Methods

    /// <summary>
    /// Reads a request from a <see cref="TcpClient"/>
    /// </summary>
    /// <param name="client">The client that sent the request</param>
    /// <returns>The request object</returns>
    protected Request ReadRequest(TcpClient client) {
        // TODO: extract stream only and forward to local function on struct

        NetworkStream inStream = client.GetStream();
        using BinaryReader reader = new(inStream, Encoding.UTF8, true);

        int methodInt = reader.ReadInt32();
        RequestMethod method;
        try {
            method = (RequestMethod)methodInt;
        } catch (InvalidCastException) {
            method = RequestMethod.INVALID;
            logger.LogWarning($"Received invalid request method {methodInt}!");
        }

        Guid clientNodeId = reader.ReadGuid();
        int clientPort = reader.ReadInt32();

        bool isBroadcast = reader.ReadBoolean();

        string route = reader.ReadString();

        uint payloadLength = reader.ReadUInt32();
        byte[] payloadBytes = reader.ReadBytes((int)payloadLength);
        string payload = Convert.ToBase64String(payloadBytes);

        byte[] originalHashBytes = reader.ReadBytes(32);
        string originalHash = Convert.ToHexString(originalHashBytes);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{methodInt}{clientPort}{route}{payloadLength}{payload}"));
        string hashString = Convert.ToHexString(hash);

        if (hashString != originalHash) {
            logger.LogWarning($"Received invalid request! Hashes don't match! Original: {originalHash} Received: {hashString}");
        }

        IPEndPoint endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        endpoint.Port = clientPort;
        if (client.Client.RemoteEndPoint is null) {
            logger.LogError($"Received request from unknown endpoint!");
            throw new NullReferenceException(nameof(client.Client.RemoteEndPoint));
        }
        return new Request {
            Method = method,
            Payload = payload,
            Route = route,
            Sender = new Address(endpoint.Address.ToString(), endpoint.Port) {
                NodeId = clientNodeId
            },
            IsBroadcast = isBroadcast
        };
    }

    /// <summary>
    /// Sends a request to a <see cref="TcpClient"/>
    /// </summary>
    /// <param name="request"></param>
    /// <param name="client"></param>
    protected void SendRequest(Request request, TcpClient client) {
        NetworkStream outStream = client.GetStream();
        using BinaryWriter writer = new(outStream, Encoding.UTF8, true);

        writer.Write((int)request.Method);
        // compact port to only necessary bits and pack isBroadcast and
        // other flags
        writer.Write(port);
        writer.Write(request.IsBroadcast);
        writer.Write(request.Route);
        byte[] payloadBytes = Convert.FromBase64String(request.Payload);
        writer.Write((uint)payloadBytes.Length);
        writer.Write(payloadBytes);

        byte[] hash = SHA256.HashData((Encoding.UTF8.GetBytes($"{(int)request.Method}{port}{(string)request.Route}{(uint)payloadBytes.Length}{request.Payload}")));
        writer.Write(hash);
    }

    /// <summary>
    /// Performs the logic for a request. This is run in the internal thread of the node.
    /// </summary>
    /// <param name="request">The Request that was sent</param>
    /// <returns>The response object</returns>
    protected abstract Response Process(Request request);

    /// <summary>
    /// Sends a response to a <see cref="TcpClient"/>
    /// </summary>
    /// <param name="response">The response that will be sent</param>
    /// <param name="client">The client that made the request and will receive the response</param>
    protected void SendResponse(Response response, TcpClient client) {
        NetworkStream outStream = client.GetStream();
        using BinaryWriter writer = new(outStream, Encoding.UTF8, true);

        writer.Write((int)response.StatusCode);
        byte[] payloadBytes = Convert.FromBase64String(response.Payload);
        writer.Write((uint)payloadBytes.Length);
        writer.Write(payloadBytes);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{(int)response.StatusCode}{(uint)payloadBytes.Length}{response.Payload}"));
        writer.Write(hash);
    }

    /// <summary>
    /// Reads a response sent from a <see cref="TcpClient"/>
    /// </summary>
    /// <param name="client">The client that received the Request and sent the Response</param>
    /// <returns></returns>
    protected Response ReadResponse(TcpClient client) {
        NetworkStream inStream = client.GetStream();
        using BinaryReader reader = new(inStream, Encoding.UTF8, true);

        StatusCode statusCode;
        try {
            statusCode = (StatusCode)reader.ReadInt32();
        } catch(InvalidCastException) {
            statusCode = StatusCode.Invalid;
            logger.LogWarning($"Received invalid status code!");
        }
        
        uint payloadLength = reader.ReadUInt32();
        byte[] payloadBytes = reader.ReadBytes((int)payloadLength);
        string payload = Convert.ToBase64String(payloadBytes);

        byte[] originalHashBytes = reader.ReadBytes(32);
        string originalHash = Convert.ToHexString(originalHashBytes);

        SHA256 sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes($"{(int)statusCode}{(uint)payloadLength}{payload}"));
        string hashString = Convert.ToHexString(hash);

        if (hashString != originalHash) {
            logger.LogWarning($"Received invalid response! Hashes don't match! Original: {originalHash} Received: {hashString}");
        }

        return new Response(statusCode, payload);
    }

    #endregion

    #region Private Methods

    private void ThreadMain() {
        tcpListener.Start();
        logger.Log($"Listening...");

        // the listen loop
        while (!cancellationTokenSource.IsCancellationRequested) {
            TcpClient incoming = tcpListener.AcceptTcpClient();

            // Read the request
            Request req = ReadRequest(incoming);

            // Process the request
            Response resp = Process(req);

            // Send the response
            if (!req.IsBroadcast) {
                SendResponse(resp, incoming);
            }

            // If the request is a broadcast, we spread it across the network
            Broadcast(req);

            // Close the connection
            logger.Log($"Closed connection with {((IPEndPoint)incoming.Client.RemoteEndPoint!).Address}");
            incoming.Close();
        }
    }

    /// <summary>
    /// Gets the peers from the bootnode
    /// </summary>
    /// <param name="bootnode">The address of the bootnode</param>
    /// <returns></returns>
    private async Task FetchPeers(Address bootnode) {
        // get the list of knowns peers from the bootnode
        using (TcpClient tcpClient = new(bootnode.Ip, bootnode.Port)) {
            SendRequest(new Request {
                Method = RequestMethod.GET,
                Route = Route.Peers
            }, tcpClient);
            Response resp = ReadResponse(tcpClient);

            if (resp.StatusCode != StatusCode.OK) {
                logger.LogError($"Failed to connect to the bootnode! Response: ${resp.StatusCode}");
                return;
            }

            Stream jsonStream = new MemoryStream(Convert.FromBase64String(resp.Payload));
            var addresses = await JsonSerializer.DeserializeAsync<List<Address>>(jsonStream);
            if (addresses is null) {
                logger.LogError($"Failed to deserialize peers!");
                return;
            }
            logger.Log($"Got {addresses.Count} peers from bootnode");
            peers = addresses;
        }

        // now we send our address to the bootnode
        using (TcpClient tcpClient = new(bootnode.Ip, bootnode.Port)) {
            string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Address("localhost", port))));
            logger.Log($"Sending our address to the bootnode...");
            SendRequest(new Request {
                Method = RequestMethod.POST,
                Route = Route.Peers_Join,
                Payload = payload,
                IsBroadcast = true
            }, tcpClient);
            lastPropagations.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))));

            //Response resp = ReadResponse(tcpClient);

            //if (resp.StatusCode != StatusCode.OK) {
            //    logger.LogWarning($"Failed to send our address to the bootnode! Response: {resp.StatusCode}");
            //}

            //tcpClient.Close();
        }
    }

    /// <summary>
    /// Spreads a broadcast across the network
    /// </summary>
    /// <param name="req">The request that was sent to this machine</param>
    private void Broadcast(Request req) {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(req.Payload)));
        if (lastPropagations.Contains(hash)) {
            logger.Log($"I have already propagated {hash}!");
            return;
        }
        lastPropagations.Add(hash);

        // we don't need to modify the request to include our own information
        // because that is automatically done in SendRequest

        Parallel.ForEach(peers, peer => {
            // we don't send the broadcast to the sender
            if (peer.Ip.Equals(req.Sender.Address) && peer.Port == req.Sender.Port) {
                logger.Log($"Skipping peer {peer} because it is the sender");
                return;
            }

            // broadcast
            TcpClient tcpClient = new(peer.Ip, peer.Port);
            logger.Log($"Broadcasting to peer {peer}...");
            SendRequest(req, tcpClient);

            // close it because we will not get a response
            tcpClient.Close();
        });
    }
    #endregion

}
