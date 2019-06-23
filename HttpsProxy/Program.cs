using System;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Collections.Generic;
using System.IO;
namespace HttpsProxy
{
    static public class AppSignal
    {
        static public bool Exit { get; set; } = false;
        static public ManualResetEvent EventExitProgram { get; set; } = new ManualResetEvent(false);
    }
    static public class Default
    {
        static public readonly int ReceivedBufferSize = 5000;
        static public readonly int SendBufferSize = 65000;
    }
    class Program
    {
        static void Main(string[] args)
        {
            ProxyListener TheProxy = new ProxyListener();
            Logging placeholder = Logging.CreateLogger();
            TheProxy.Start("Listener");
            Console.ReadKey();

            AppSignal.Exit = true;
            AppSignal.EventExitProgram.Set();
            Logging.Log("Exit");
            TcpClient StopProxy = new TcpClient();
            StopProxy.Connect(new IPEndPoint(IPAddress.Parse("127.0.0.1"), TheProxy.IPEndPoint.Port));
            StopProxy.Close();
        }
    }

    public abstract class Worker
    {
        private Thread m_worker = null;
        public virtual bool Start(string Name = "Worker",bool IsBackground = false)
        {
            if (InitInstance())
            {
                m_worker = new Thread(Run)
                {
                    IsBackground = IsBackground,
                    Name = Name ?? "Worker"
                };
                m_worker.Start();
                return true;
            }
            return false;
        }
        protected virtual bool InitInstance() { return true; }
        protected abstract void Run();
        protected virtual void ExitInstance() {}
        public ThreadState GetState()
        {
            if (m_worker == null) return ThreadState.Unstarted;
            return m_worker.ThreadState;
        }
        public void Abort()
        {
            if (m_worker != null) m_worker.Abort();
        }
        public bool Join(int milliseconds = Timeout.Infinite)
        {
            if (m_worker != null) return m_worker.Join(milliseconds);
            return true;
        }

    }
    public sealed class ProxyListener : Worker
    {
        private TcpListener m_tcpListener = null;
        public IPEndPoint IPEndPoint { get; private set; } = new IPEndPoint(IPAddress.Any, 1234);
        protected override bool InitInstance()
        {
            m_tcpListener = new TcpListener(IPEndPoint);
            m_tcpListener.Start();
            return base.InitInstance();
        }
        protected override void ExitInstance()
        {
            m_tcpListener.Stop();
        }
        protected override void Run()
        {
            while (!AppSignal.Exit)
            {
                // Accepting pending connection
                TcpClient m_tcpClient = m_tcpListener.AcceptTcpClient();

                // Attach to a new thread which determines whether it is an http connection or https connection
                ProtocolDistributor distributor = new ProtocolDistributor(m_tcpClient);

                // Make this thread background so connection with client will be closed automatically when the thread is destroyed
                if (distributor.Start("ProtocolDistributor", true) == false)
                    Logging.Log("Null Instance of TcpClient detected in ProxyListener.Run()", LoggingLevel.Error);
            }
            ExitInstance();
        }
    }

    public class StreamState
    {
        public Stream ClientStream { get; set; } = null;
        public Stream ServerStream { get; set; } = null;
        public byte[] Buffer { get; set; } = null;
    }
    public static class ProxyOperation
    {
        public static List<byte> ReadRequest(NetworkStream source)
        {
            try
            {
                List<byte> result = new List<byte>();
                byte[] buffer = new byte[Default.ReceivedBufferSize];

                do
                {
                    int bytesRead = source.Read(buffer, 0, buffer.Length);

                    // Expand array as much as bytes read
                    for (int i = 0; i < bytesRead; ++i)
                        result.Add(buffer[i]);

                } while (source.DataAvailable);

                return result;
            }
            catch (System.IO.IOException e)
            {
                if(e.InnerException is SocketException sErr)
                {
                    if (sErr.ErrorCode == 10053 || sErr.ErrorCode == 10054)
                        return new List<byte>();
                    throw sErr;
                }
                throw e;
            }
            catch (Exception) // null, out of range, disposed
            {
                return new List<byte>();
            }
        }
        public static int Write(NetworkStream dest, byte[] buffer, int offset, int size)
        {
            try
            {
                dest.Write(buffer, offset, size);
                return size;
            }
            catch (System.IO.IOException e)
            {
                if (e.InnerException is SocketException sErr)
                {
                    if (sErr.ErrorCode == 10053 || sErr.ErrorCode == 10054)
                        return -sErr.ErrorCode;
                    throw sErr;
                }
                throw e;
            }
            catch (Exception) // null, out of range, disposed
            {
                return -1;
            }
        }
    }

    public sealed class ProtocolDistributor : Worker
    {
        static private readonly List<string> SupportedMethods = new List<string>()
        {
            "CONNECT","GET","POST"
        };
        public const int ReceivedBufferSize = 5000;
        private TcpClient m_client = null;
        static private string GetMethod(string raw_request)
        {
            try
            {
                if (string.IsNullOrEmpty(raw_request)) return null;
                if (string.IsNullOrWhiteSpace(raw_request)) return null;

                return raw_request.Substring(0, raw_request.IndexOf(' '));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public ProtocolDistributor(TcpClient tcp)
        {
            m_client = tcp;
        }

        protected override bool InitInstance()
        {
            try
            {
                //Setup socket
                m_client.ReceiveBufferSize = 5000;

                return base.InitInstance();
            }
            catch (Exception)
            {
                return false;
            }
        }
        protected override void ExitInstance()
        {
            if (m_client != null) m_client.Close();
        }
        protected override void Run()
        {
            // Read bytes and traslate client's request into string
            List<byte> ReceivedBytes = ProxyOperation.ReadRequest(m_client.GetStream());
            string rawRequest = Encoding.ASCII.GetString(ReceivedBytes.ToArray());

            if (string.IsNullOrEmpty(rawRequest) == false)
            {
                Logging.Log(rawRequest);

                // parsing request and extract its method
                string RequestMethod = GetMethod(rawRequest);

                // Check if request's method is supported
                if (SupportedMethods.IndexOf(RequestMethod) < 0)
                {
                    byte[] response = Encoding.ASCII.GetBytes("501 Not Implemented\r\n\r\n");
                    m_client.GetStream().Write(response, 0, response.Length);
                }
                else
                {
                    // Create Protocol Processor denpending on what method is received
                    if (RequestMethod == "GET" || RequestMethod == "POST")
                    {
                        HttpClient processor = new HttpClient(m_client, rawRequest);
                        processor.Start("Http Handler", true);
                    }
                    else
                    {
                        HttpsClient processor = new HttpsClient(m_client, rawRequest);
                        processor.Start("Https Handler", true);
                    }
                    m_client = null;
                }
            }
            ExitInstance();
        }
    }
    public sealed class HttpClient : Worker
    {
        static public int InstanceCount { get; private set; } = 0;
        private string m_request = null;
        private TcpClient m_client = null;

        public HttpClient(TcpClient tcp, string firstRequestString)
        {
            m_request = firstRequestString;
            m_client = tcp;
        }

        protected override bool InitInstance()
        {
            if (string.IsNullOrEmpty(m_request) || string.IsNullOrWhiteSpace(m_request)) return false;
            if (m_client == null) return false;

            ++InstanceCount;
            Logging.Log(string.Format("HTTP Instances: {0}", InstanceCount), LoggingLevel.Warning);
            return base.InitInstance();
        }
        protected override void ExitInstance()
        {
            --InstanceCount;
            Logging.Log(string.Format("1 HTTP instance exited, remain: {0}", InstanceCount), LoggingLevel.Warning);
            m_client.Close();
        }
        protected override void Run()
        {
            StreamState streamState = new StreamState()
            {
                Buffer = new byte[m_client.SendBufferSize],
                ClientStream = m_client.GetStream(),
                ServerStream = null,
            };

            Thread DoTransfer = new Thread(DoTransferData)
            {
                Name = "Transfer Data",
                IsBackground = true
            };

            // Extract remote host endpoint
            TcpClient server = null;
            try
            {
                string DomainEndPoint = GetHostHeader(m_request);
                IPEndPoint RemoteEndPoint = GetRemoteHostEndPoint(DomainEndPoint);

                // Connect to remote host by received IPEndPoint
                server = new TcpClient();
                server.Connect(RemoteEndPoint);

                streamState.ServerStream = server.GetStream();
                DoTransfer.Start(streamState);
            }
            catch (ArgumentNullException)
            {
                ExitInstance();
                return;
            }

            List<byte> ReceivedBytes = new List<byte>(Encoding.ASCII.GetBytes(m_request));
            do
            {

                int ErrorCode = ProxyOperation.Write(server.GetStream(), ReceivedBytes.ToArray(), 0, ReceivedBytes.Count);
                if (ErrorCode < 0)
                {
                    // An error has occurred => proceed to close thread
                    Logging.Log(string.Format("SocketException occured with error code: {0}", ErrorCode), LoggingLevel.Error);
                    server.Close();
                    m_client.Close();
                }
                ReceivedBytes = ProxyOperation.ReadRequest(m_client.GetStream());

            } while (ReceivedBytes.Count != 0);

            ExitInstance();
        }
        private void DoTransferData(object state)
        {
            StreamState streamState = state as StreamState;
            if (streamState == null) throw new ArgumentNullException();

            NetworkStream WStream = streamState.ClientStream as NetworkStream;
            NetworkStream RStream = streamState.ServerStream as NetworkStream;

            while (true)
            {
                try
                {
                    int bytesRead = RStream.Read(streamState.Buffer, 0, streamState.Buffer.Length);
                    WStream.WriteAsync(streamState.Buffer, 0, bytesRead);
                }
                catch (System.IO.IOException e)
                {
                    if (e.InnerException is SocketException sErr)
                    {
                        if (sErr.ErrorCode == 10053 || sErr.ErrorCode == 10054 || sErr.ErrorCode == 10058)
                            return;
                        throw sErr;
                    }
                    else if(e.InnerException is ObjectDisposedException)
                    {
                        return;
                    }
                    throw e;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
            }
        }
        static public IPEndPoint GetRemoteHostEndPoint(string DomainEndPoint)
        {
            if (string.IsNullOrEmpty(DomainEndPoint)) return null;
            string Port = null;

            int i = DomainEndPoint.IndexOf(':');
            if (i < 0)
            {
                Port = "80";
                i = DomainEndPoint.Length;
            }
            else
                Port = DomainEndPoint.Substring(DomainEndPoint.IndexOf(':') + 1);
            string HostName = DomainEndPoint.Substring(0, i);
            try
            {
                // Get IPv4 address
                IPAddress[] addresses = Dns.GetHostAddresses(HostName);
                return new IPEndPoint(addresses[addresses.Length - 1], Convert.ToInt32(Port));
            }
            catch (Exception)
            {
                return null;
            }
        }
        static public string GetHostHeader(string raw_request)
        {
            if (string.IsNullOrEmpty(raw_request)) return null;
            if (string.IsNullOrWhiteSpace(raw_request)) return null;

            int HostStart = raw_request.IndexOf("Host: ");
            if (HostStart < 0) return null;
            int HostEnd = raw_request.IndexOf('\r', HostStart + 6);

            // DomainEndPoint's format: name:port, e.g: facebook.com:443
            string DomainEndPoint = raw_request.Substring(HostStart + 6, HostEnd - HostStart - 6);
            return DomainEndPoint;
        }
    }

    public sealed class HttpsClient : Worker
    {
        public static int InstanceCount { get; private set; } = 0;
        private string m_request = null;
        private TcpClient m_client = null;
        public HttpsClient(TcpClient tcp, string firstRequest)
        {
            m_request = firstRequest;
            m_client = tcp;
        }

        protected override bool InitInstance()
        {
            if (string.IsNullOrEmpty(m_request) || string.IsNullOrWhiteSpace(m_request)) return false;
            if (m_client == null) return false;

            ++InstanceCount;
            Logging.Log(string.Format("HTTPS instances: {0}", InstanceCount),LoggingLevel.Warning);
            return base.InitInstance();
        }
        protected override void ExitInstance()
        {
            m_client.Close();
            --InstanceCount;
            Logging.Log(string.Format("HTTPS remain: {0}", InstanceCount), LoggingLevel.Warning);
            base.ExitInstance();
        }
        protected override void Run()
        {
            // Get host EndPoint
            string[] domain = GetDomain(m_request);
            IPEndPoint hostEndPoint = GetHostEndPoint(domain[0], domain[1]);
            if(hostEndPoint == null)
            {
                ExitInstance();
                return;
            }

            // Connect to remote host and authenticate connection
            TcpClient server = new TcpClient();
            server.Connect(hostEndPoint);

            // Accept tunneling
            byte[] AcceptConnection = Encoding.ASCII.GetBytes("HTTP/1.1 200 Established\r\n\r\n");
            ProxyOperation.Write(m_client.GetStream(), AcceptConnection, 0, AcceptConnection.Length);

            StreamState streamState = new StreamState()
            {
                ClientStream = m_client.GetStream(),
                ServerStream = server.GetStream(),
            };

            Thread DoTransfer = new Thread(DoTransferData)
            {
                Name = "HTTPS Transfer Data",
                IsBackground = true
            };
            DoTransfer.Start(streamState);

            List<byte> ReceivedBytes;
            do
            {
                ReceivedBytes = ProxyOperation.ReadRequest(m_client.GetStream());
                int errCode = ProxyOperation.Write(server.GetStream(), ReceivedBytes.ToArray(), 0, ReceivedBytes.Count);
                if(errCode < 0)
                {
                    ReceivedBytes.Clear();
                    Logging.Log(string.Format("Socket Exception with error code: {0}", errCode), LoggingLevel.Error);
                }
            } while (ReceivedBytes.Count != 0);
            ExitInstance();

        }
        /// <summary>
        /// Read response from server and forward to client
        /// </summary>
        /// <param name="objState"></param>
        private void DoTransferData(object objState)
        {
            StreamState streamState = objState as StreamState;
            NetworkStream readStream = streamState.ServerStream as NetworkStream;
            NetworkStream writeStream = streamState.ClientStream as NetworkStream;
            byte[] buffer = new byte[Default.SendBufferSize];
            try
            {
                while (true)
                {
                    int bytesRead = readStream.Read(buffer, 0, buffer.Length);
                    ProxyOperation.Write(writeStream, buffer, 0, bytesRead);
                }
            }
            catch (IOException e)
            {
                if (e.InnerException is SocketException sErr)
                {
                    if (sErr.ErrorCode == 10053 || sErr.ErrorCode == 10054)
                    {
                        return;
                    }
                    throw sErr;
                }
                throw e;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
 
        }
        private string[] GetDomain(string rawRequest)
        {
            string hostName, port;
            int hostStart = rawRequest.IndexOf(' ') + 1;
            int hostEnd = rawRequest.IndexOf(':', hostStart);
            int portEnd = rawRequest.IndexOf(' ', hostEnd + 1);
            try
            {
                hostName = rawRequest.Substring(hostStart, hostEnd - hostStart);
                port = rawRequest.Substring(hostEnd + 1, portEnd - hostEnd - 1);
            }
            catch (ArgumentOutOfRangeException)
            {
                hostName = port = string.Empty;
            }
            return new string[2] { hostName, port };
        }
        private IPEndPoint GetHostEndPoint(string hostName, string port)
        {
            try
            {
                // Get IPv4 address
                IPAddress[] addresses = Dns.GetHostAddresses(hostName);
                return new IPEndPoint(addresses[addresses.Length - 1], Convert.ToInt32(port));
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
