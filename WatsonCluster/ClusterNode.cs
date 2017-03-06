﻿using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatsonCluster
{
    /// <summary>
    /// A Watson cluster node, which includes both a cluster server and client.
    /// </summary>
    public class ClusterNode : IDisposable
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private int LocalPort;
        private string PeerIp;
        private int PeerPort;
        private bool Debug;
        private ClusterServer Server;
        private ClusterClient Client;

        private Func<bool> ClusterHealthy;
        private Func<bool> ClusterUnhealthy;
        private Func<byte[], bool> MessageReceived;
        private Func<string, bool> NonPeerClientConnected;
        private Func<string, bool> NonPeerClientDisconnected;
        private Func<string, bool> ClusterPeerReplaced;

        private string CurrPeerIpPort;
        private readonly SemaphoreSlim PeerUpdateSemSlm, NonPeerUpdateSemSlm;
        private readonly HashSet<string> OldPeerClientIpPorts, NonPeerClientIpPorts;
        private readonly HashSet<string> AllowedPeerClientIps;

        private static readonly char[] singleColonSplit = { ':' };

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Start the cluster node.
        /// </summary>
        /// <param name="peerIp">The IP address of the peer cluster node.</param>
        /// <param name="peerPort">The TCP port of the peer cluster node.</param>
        /// <param name="localPort">The TCP port on which the cluster server should listen.</param>
        /// <param name="clusterHealthy">Function to be called when the cluster becomes healthy.</param>
        /// <param name="clusterUnhealthy">Function to be called when the cluster becomes unhealthy.</param>
        /// <param name="messageReceived">Function to be called when a message is received from the peer.</param>
        /// <param name="debug">Enable or disable debug logging to the console.</param>
        public ClusterNode(
            string peerIp, 
            int peerPort, 
            int localPort, 
            Func<bool> clusterHealthy, 
            Func<bool> clusterUnhealthy, 
            Func<byte[], bool> messageReceived,
            bool debug = false, // produces console output

            IEnumerable<string> alternateClientPeerIps = null, // walked once

            // optional callbacks for non-peer clients
            Func<string, bool> nonPeerClientConnected = null,
            Func<string, bool> nonPeerClientDisconnected = null,
            Func<string, bool> clusterPeerReplaced = null
            )
        {
            if (String.IsNullOrEmpty(peerIp)) throw new ArgumentNullException(nameof(peerIp));
            if (peerPort < 1) throw new ArgumentOutOfRangeException(nameof(peerPort));
            if (localPort < 1) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (clusterHealthy == null) throw new ArgumentNullException(nameof(clusterHealthy));
            if (clusterUnhealthy == null) throw new ArgumentNullException(nameof(clusterUnhealthy));
            if (messageReceived == null) throw new ArgumentNullException(nameof(messageReceived));

            PeerIp = peerIp;
            PeerPort = peerPort;
            LocalPort = localPort;
            ClusterHealthy = clusterHealthy;
            ClusterUnhealthy = clusterUnhealthy;
            MessageReceived = messageReceived;
            Debug = debug;

            ClusterPeerReplaced = clusterPeerReplaced;
            NonPeerClientConnected = nonPeerClientConnected;
            NonPeerClientDisconnected = nonPeerClientDisconnected;

            // Value not currently used, but preferred over HashSet due to concurrency
            OldPeerClientIpPorts = new HashSet<string>();
            NonPeerClientIpPorts = new HashSet<string>();
            PeerUpdateSemSlm = new SemaphoreSlim(1);
            NonPeerUpdateSemSlm = new SemaphoreSlim(1);

            AllowedPeerClientIps = new HashSet<string>();
            if (!string.IsNullOrEmpty(PeerIp))
                AllowedPeerClientIps.Add(PeerIp);
            if (alternateClientPeerIps != null)
                AllowedPeerClientIps.UnionWith(alternateClientPeerIps);

            Server = new ClusterServer(LocalPort, Debug, SrvClientConnect, SrvClientDisconnect, SrvMsgReceived);
            Client = new ClusterClient(PeerIp, PeerPort, Debug, CliServerConnect, CliServerDisconnect, CliMsgReceived);
        }

        #endregion

        #region Public-Methods

        /// <summary>
        /// Determine if the cluster is healthy (i.e. both nodes are bidirectionally connected).
        /// </summary>
        /// <returns>True if healthy.</returns>
        public bool IsHealthy()
        {
            if (Server == null)
            {
                if (Debug) Console.WriteLine("Server object is null");
                return false;
            }
            
            if (Client == null)
            {
                if (Debug) Console.WriteLine("Client object is null");
                return false;
            }

            if (String.IsNullOrEmpty(CurrPeerIpPort))
            {
                if (Debug) Console.WriteLine("Peer information is null");
                return false;
            }

            if (!Server.IsConnected(CurrPeerIpPort))
            {
                if (Debug) Console.WriteLine("Server peer is not connected");
                return false;
            }

            if (!Client.IsConnected())
            {
                if (Debug) Console.WriteLine("Client is not connected");
                return false;
            }

            return true;
        }
        
        /// <summary>
        /// Send a message to the peer node.
        /// </summary>
        /// <param name="data">Data to send to the peer node.</param>
        /// <returns>True if successful.</returns>
        public bool Send(byte[] data)
        {
            if (Client != null)
            {
                if (Client.IsConnected())
                {
                    return Client.Send(data);
                }
            }
            else if (String.IsNullOrEmpty(CurrPeerIpPort))
            {
                if (Debug) Console.WriteLine("No peer connected");
                return false;
            }
            else if (Server != null)
            {
                if (Server.IsConnected(CurrPeerIpPort))
                {
                    return Server.Send(CurrPeerIpPort, data);
                }
            }

            if (Debug) Console.WriteLine("Neither server or client are healthy");
            return false;
        }

        /// <summary>
        /// Send a message to the peer node, asynchronously.
        /// </summary>
        /// <param name="data">Data to send to the peer node.</param>
        /// <returns>Task with Boolean indicating if the message was sent successfully.</returns>
        public async Task<bool> SendAsync(byte[] data)
        {
            if (Client != null)
            {
                if (Client.IsConnected())
                {
                    return await Client.SendAsync(data);
                }
            }
            else if (String.IsNullOrEmpty(CurrPeerIpPort))
            {
                if (Debug) Console.WriteLine("No peer connected");
                return false;
            }
            else if (Server != null)
            {
                if (Server.IsConnected(CurrPeerIpPort))
                {
                    return await Server.SendAsync(CurrPeerIpPort, data);
                }
            }

            if (Debug) Console.WriteLine("Neither server or client are healthy");
            return false;
        }

        /// <summary>
        /// Destroy the cluster node and release resources.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        #endregion

        #region Private-Methods

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (Server != null) Server.Dispose();
                if (Client != null) Client.Dispose();
            }
        }

        private bool SrvClientConnect(string ipPort)
        {
            if (AllowedPeerClientIps.Contains(ipPort.Split(singleColonSplit, 2)[0]))
            {
                PeerUpdateSemSlm.Wait();
                if (CurrPeerIpPort != null && CurrPeerIpPort != ipPort)
                    OldPeerClientIpPorts.Add(CurrPeerIpPort);

                CurrPeerIpPort = ipPort;
                PeerUpdateSemSlm.Release();

                if (Client != null && Client.IsConnected())
                    ClusterHealthy();
            }
            else
            {
                NonPeerUpdateSemSlm.Wait();
                bool added = NonPeerClientIpPorts.Add(ipPort);
                NonPeerUpdateSemSlm.Release();
                if (added)
                   NonPeerClientConnected?.Invoke(ipPort);
            }

            return true;
        }

        private bool SrvClientDisconnect(string ipPort)
        {
            if (ipPort == CurrPeerIpPort)
            {
                PeerUpdateSemSlm.Wait();
                CurrPeerIpPort = null;
                PeerUpdateSemSlm.Release();

                ClusterUnhealthy();
            }
            else if (NonPeerClientIpPorts.Contains(ipPort))
            {
                NonPeerUpdateSemSlm.Wait();
                bool removed = NonPeerClientIpPorts.Remove(ipPort);
                NonPeerUpdateSemSlm.Release();
                if (removed)
                    NonPeerClientDisconnected?.Invoke(ipPort);
            }
            else
            {
                PeerUpdateSemSlm.Wait();
                OldPeerClientIpPorts.Remove(ipPort);
                PeerUpdateSemSlm.Release();
            }
            return true;
        }

        private bool SrvMsgReceived(string ipPort, byte[] data)
        {
            if (ipPort != CurrPeerIpPort)
            {
                // ignoring everyone else for now
                return true;
            }

            MessageReceived(data);
            return true;
        }

        private bool CliServerConnect()
        {
            if (Server != null
                && !string.IsNullOrEmpty(CurrPeerIpPort)
                && Server.IsConnected(CurrPeerIpPort))
            {
                ClusterHealthy();
            }
            return true;
        }

        private bool CliServerDisconnect()
        {
            ClusterUnhealthy();
            return true;
        }

        private bool CliMsgReceived(byte[] data)
        {
            MessageReceived(data);
            return true;
        }

        #endregion
    }
}
