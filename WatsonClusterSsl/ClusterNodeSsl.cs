﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WatsonClusterSsl
{
    /// <summary>
    /// A Watson cluster node with SSL, which includes both a cluster server and client.
    /// </summary>
    public class ClusterNodeSsl : IDisposable
    {
        #region Public-Members

        #endregion

        #region Private-Members

        private int LocalPort;
        private string PeerIp;
        private int PeerPort;
        private string CertFile;
        private string CertPass;
        private bool AcceptInvalidCerts;
        private bool Debug;
        private ClusterServerSsl Server;
        private ClusterClientSsl Client;

        private Func<bool> ClusterHealthy;
        private Func<bool> ClusterUnhealthy;
        private Func<byte[], bool> MessageReceived;

        private string CurrPeerIpPort;

        #endregion

        #region Constructors-and-Factories

        /// <summary>
        /// Start the cluster node.
        /// </summary>
        /// <param name="peerIp">The IP address of the peer cluster node.</param>
        /// <param name="peerPort">The TCP port of the peer cluster node.</param>
        /// <param name="localPort">The TCP port on which the cluster server should listen.</param>
        /// <param name="certFile">The PFX file containing the certificate.</param>
        /// <param name="certPass">The password to the certificate.</param>
        /// <param name="acceptInvalidCerts">True to accept invalid SSL certificates.</param>
        /// <param name="clusterHealthy">Function to be called when the cluster becomes healthy.</param>
        /// <param name="clusterUnhealthy">Function to be called when the cluster becomes unhealthy.</param>
        /// <param name="messageReceived">Function to be called when a message is received from the peer.</param>
        /// <param name="debug">Enable or disable debug logging to the console.</param>
        public ClusterNodeSsl(
            string peerIp,
            int peerPort,
            int localPort,
            string certFile, 
            string certPass, 
            bool acceptInvalidCerts,
            Func<bool> clusterHealthy,
            Func<bool> clusterUnhealthy,
            Func<byte[], bool> messageReceived,
            bool debug)
        {
            if (String.IsNullOrEmpty(peerIp)) throw new ArgumentNullException(nameof(peerIp));
            if (peerPort < 1) throw new ArgumentOutOfRangeException(nameof(peerPort));
            if (localPort < 1) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (String.IsNullOrEmpty(certFile)) throw new ArgumentNullException(nameof(certFile));
            if (clusterHealthy == null) throw new ArgumentNullException(nameof(clusterHealthy));
            if (clusterUnhealthy == null) throw new ArgumentNullException(nameof(clusterUnhealthy));
            if (messageReceived == null) throw new ArgumentNullException(nameof(messageReceived));

            PeerIp = peerIp;
            PeerPort = peerPort;
            LocalPort = localPort;
            CertFile = certFile;
            CertPass = certPass;
            AcceptInvalidCerts = acceptInvalidCerts;
            ClusterHealthy = clusterHealthy;
            ClusterUnhealthy = clusterUnhealthy;
            MessageReceived = messageReceived;
            Debug = debug;

            Server = new ClusterServerSsl(PeerIp, LocalPort, CertFile, CertPass, AcceptInvalidCerts, Debug, SrvClientConnect, SrvClientDisconnect, SrvMsgReceived);
            Client = new ClusterClientSsl(PeerIp, PeerPort, CertFile, CertPass, AcceptInvalidCerts, Debug, CliServerConnect, CliServerDisconnect, CliMsgReceived);
        }

        /// <summary>
        /// Start the cluster node.
        /// </summary>
        /// <param name="peerIp">The IP address of the peer cluster node.</param>
        /// <param name="peerPort">The TCP port of the peer cluster node.</param>
        /// <param name="localPort">The TCP port on which the cluster server should listen.</param>
        /// <param name="certFile">The PFX file containing the certificate.</param>
        /// <param name="certPass">The password to the certificate.</param>
        /// <param name="acceptInvalidCerts">True to accept invalid SSL certificates.</param>
        /// <param name="permittedIps">The list of IP addresses allowed to connect.</param>
        /// <param name="clusterHealthy">Function to be called when the cluster becomes healthy.</param>
        /// <param name="clusterUnhealthy">Function to be called when the cluster becomes unhealthy.</param>
        /// <param name="messageReceived">Function to be called when a message is received from the peer.</param>
        /// <param name="debug">Enable or disable debug logging to the console.</param>
        public ClusterNodeSsl(
            string peerIp,
            int peerPort,
            int localPort,
            string certFile,
            string certPass,
            bool acceptInvalidCerts,
            IEnumerable<string> permittedIps,
            Func<bool> clusterHealthy,
            Func<bool> clusterUnhealthy,
            Func<byte[], bool> messageReceived,
            bool debug)
        {
            if (String.IsNullOrEmpty(peerIp)) throw new ArgumentNullException(nameof(peerIp));
            if (peerPort < 1) throw new ArgumentOutOfRangeException(nameof(peerPort));
            if (localPort < 1) throw new ArgumentOutOfRangeException(nameof(localPort));
            if (String.IsNullOrEmpty(certFile)) throw new ArgumentNullException(nameof(certFile));
            if (clusterHealthy == null) throw new ArgumentNullException(nameof(clusterHealthy));
            if (clusterUnhealthy == null) throw new ArgumentNullException(nameof(clusterUnhealthy));
            if (messageReceived == null) throw new ArgumentNullException(nameof(messageReceived));

            PeerIp = peerIp;
            PeerPort = peerPort;
            LocalPort = localPort;
            CertFile = certFile;
            CertPass = certPass;
            AcceptInvalidCerts = acceptInvalidCerts;
            ClusterHealthy = clusterHealthy;
            ClusterUnhealthy = clusterUnhealthy;
            MessageReceived = messageReceived;
            Debug = debug;

            List<string> PermittedIps = null;
            if (permittedIps != null && permittedIps.Count() > 0) permittedIps = new List<string>(permittedIps);

            Server = new ClusterServerSsl(PermittedIps, LocalPort, CertFile, CertPass, AcceptInvalidCerts, Debug, SrvClientConnect, SrvClientDisconnect, SrvMsgReceived);
            Client = new ClusterClientSsl(PeerIp, PeerPort, CertFile, CertPass, AcceptInvalidCerts, Debug, CliServerConnect, CliServerDisconnect, CliMsgReceived);
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

            if (Client.IsConnected())
            {
                if (Debug) Console.WriteLine("Client is not connected");
                return true;
            }

            return false;
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
            CurrPeerIpPort = ipPort;
            if (Client != null
                && Client.IsConnected())
            {
                ClusterHealthy();
            }
            return true;
        }

        private bool SrvClientDisconnect(string ipPort)
        {
            CurrPeerIpPort = null;
            ClusterUnhealthy();
            return true;
        }

        private bool SrvMsgReceived(string ipPort, byte[] data)
        {
            MessageReceived(data);
            return true;
        }

        private bool CliServerConnect()
        {
            if (Server != null
                && !String.IsNullOrEmpty(CurrPeerIpPort)
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
