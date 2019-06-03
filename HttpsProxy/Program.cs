using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Collections.Generic;
namespace HttpsProxy
{
    static public class AppSignal
    {
        static public bool Exit { get; set; } = false;
        static public ManualResetEvent EventExitProgram { get; set; } = new ManualResetEvent(false);
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
                    Logging.Log("Null Instance of TcpClient detected in ProxyListener.Run()", LoggingLevel.Warning);
            }
            ExitInstance();
        }
    }

    public class StreamState
    {
        public NetworkStream ReadStream { get; set; } = null;
        public NetworkStream WriteStream { get; set; } = null;
        public byte[] Buffer { get; set; } = null;
        public StringBuilder StrBuilder { get; set; } = null;
    }
    public sealed class ProtocolDistributor : Worker
    {
        static private readonly List<string> SupportedMethods = new List<string>()
        {
            "CONNECT","GET","POST"
        };

        private TcpClient m_client = null;
        ManualResetEvent m_EventReadDone = null;
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
                m_EventReadDone = new ManualResetEvent(false);

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
            if (m_client == null || AppSignal.Exit)
            {
                ExitInstance();
                return;
            }

            // Read bytes and traslate client's request into string
            StreamState ObjectState = new StreamState()
            {
                Buffer = new byte[m_client.ReceiveBufferSize],
                ReadStream = m_client.GetStream(),
                StrBuilder = new StringBuilder()
            };
            IAsyncResult Result = m_client.GetStream().BeginRead(ObjectState.Buffer, 0, ObjectState.Buffer.Length, new AsyncCallback(ReadCallBack), ObjectState);

            // Wait until finishing reading client's request
            m_EventReadDone.WaitOne();

            // StringBuilder may be null due to connection is closed
            if (ObjectState.StrBuilder == null)
            {
                ExitInstance();
                return;
            }

            string FirstRequest = ObjectState.StrBuilder.ToString();
            Logging.Log(FirstRequest);

            // parsing request and extract its method
            string RequestMethod = GetMethod(FirstRequest);

            // Check if request's method is supported
            if (SupportedMethods.IndexOf(RequestMethod) < 0)
            {
                byte[] response = Encoding.ASCII.GetBytes("501 Not Implemented\r\n\r\n");
                m_client.GetStream().Write(response, 0, response.Length);
            }
            else
            {
                // Create Protocol Processor denpending on what method is received
                if(RequestMethod == "GET" || RequestMethod == "POST")
                {
                    HttpClient processor = new HttpClient(m_client, FirstRequest);
                    processor.Start("Http");
                }
                else
                {
                    HttpsClient processor = new HttpsClient(m_client, FirstRequest);
                    processor.Start("Https");
                }
                m_client = null;
            }
            ExitInstance();
        }

        private void ReadCallBack(IAsyncResult ar)
        {
            // Get object state
            StreamState ObjectState = ar.AsyncState as StreamState;

            NetworkStream stream = ObjectState.ReadStream;
            StringBuilder builder = ObjectState.StrBuilder;
            byte[] buffer = ObjectState.Buffer;

            // Get bytes read from client
            try
            {
                int byteRead = stream.EndRead(ar);

                // Translate data into string
                builder.Append(Encoding.ASCII.GetString(buffer, 0, byteRead));

                Array.Clear(buffer, 0, byteRead);
                if (stream.DataAvailable)
                {
                    // Read until there is no more data to be transferred
                    stream.BeginRead(buffer, 0, buffer.Length, new AsyncCallback(ReadCallBack), ObjectState);
                }
                else m_EventReadDone.Set();
            }
            catch (System.IO.IOException e)
            {
                if (e.InnerException is SocketException sError)
                {
                    if (sError.SocketErrorCode == SocketError.ConnectionAborted ||
                        sError.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        Logging.Log(sError.Message, LoggingLevel.Error);

                        // Connection with client has been closed,
                        // returned request will be null indicating closed connection
                        builder = null;
                        m_EventReadDone.Set();
                        return;
                    }
                    throw sError;
                }
                throw e;
            }
        }
    }
    public sealed class HttpClient : Worker
    {
        private string m_request = null;
        private TcpClient m_client = null;

        private AutoResetEvent m_EventReadDone = null;
        struct DomainEndPoint
        {
            public byte[] SentBytes;
            public IPEndPoint IP;
            public NetworkStream ClientStream;
        }
        public HttpClient(TcpClient tcp, string firstRequestString)
        {
            m_request = firstRequestString;
            m_client = tcp;
        }

        protected override bool InitInstance()
        {
            if (string.IsNullOrEmpty(m_request) || string.IsNullOrWhiteSpace(m_request)) return false;
            if (m_client == null) return false;

            // Already had request string
            m_EventReadDone = new AutoResetEvent(false);

            return base.InitInstance();
        }
        protected override void ExitInstance()
        {
            m_client.Close();
        }
        protected override void Run()
        {
            // Handle WSA_IO_PENDING

            // Setup working thread
            Thread ReadRequest = new Thread(DoReceiveRequest)
            {
                IsBackground = true,
                Name = "Read Request",
            };

            StreamState state = new StreamState()
            {
                Buffer = new byte[m_client.ReceiveBufferSize],
                ReadStream = m_client.GetStream(),
                StrBuilder = new StringBuilder(m_request)
            };

            ReadRequest.Start(state);

            //
            // Parse request
            //

            //
            // Get remote server addresses
            //

            //
            // Connect and send request to remote host, redirect host's response to client
            //

            //
            // Receive request
            //
        }

        private void DoReceiveRequest(object state)
        {
            StreamState ReceivedState = (StreamState)state;
            while (true)
            {
                // Extract remote host endpoint

                if (ReceivedState.StrBuilder == null) return;
                string DomainEndPoint = GetHostHeader(ReceivedState.StrBuilder.ToString());
                IPEndPoint RemoteEndPoint = GetRemoteHostEndPoint(DomainEndPoint);

                // Create thread to handle client's request
                if (RemoteEndPoint != null)
                {
                    DomainEndPoint domain = new DomainEndPoint()
                    {
                        IP = RemoteEndPoint,
                        SentBytes = Encoding.ASCII.GetBytes(ReceivedState.StrBuilder.ToString()),
                        ClientStream = m_client.GetStream()
                    };

                    Thread HandleClient = new Thread(DoConnectAndRedirect)
                    {
                        IsBackground = true,
                        Name = "Handling Request",
                        Priority = ThreadPriority.Highest
                    };
                    HandleClient.Start(domain);
                }
                else return;    //Client has closed connection => Exit thread

                // Prepare new request by clearing string builder
                ReceivedState.StrBuilder.Clear();

                // Read Client's request
                ReceivedState.ReadStream.BeginRead(ReceivedState.Buffer, 0, ReceivedState.Buffer.Length, ReadRequestCallBack, ReceivedState);
                m_EventReadDone.WaitOne();
            }
        }
        private void DoConnectAndRedirect(object state)
        {
            DomainEndPoint domain = (DomainEndPoint)state;
            if (domain.SentBytes == null || domain.IP == null || domain.ClientStream == null) throw new ArgumentNullException();

            // Connect and send request to server
            TcpClient server = new TcpClient();
            server.Connect(domain.IP);
            server.GetStream().Write(domain.SentBytes, 0, domain.SentBytes.Length);

            //
            // Wait for parsing process finished, do checking cache, list of allowed domains, modify response.... -> Not Implemented
            //

            StreamState received = new StreamState()
            {
                Buffer = new byte[server.ReceiveBufferSize],
                ReadStream = server.GetStream(),
                WriteStream = domain.ClientStream,
                StrBuilder = null
            };

            // Transfer data from remote host to client
            server.GetStream().BeginRead(received.Buffer, 0, received.Buffer.Length, ReadResponseCallBack, received);
        }

        private void ReadRequestCallBack(IAsyncResult ar)
        {
            // Get object state
            StreamState ObjectState = ar.AsyncState as StreamState;
            StringBuilder builder = ObjectState.StrBuilder;
            NetworkStream stream = ObjectState.ReadStream;
            byte[] buffer = ObjectState.Buffer;

            try
            {
                // Get bytes read from client
                int byteRead = stream.EndRead(ar);

                // Gracefully closed from client
                if (byteRead == 0)
                {
                    ObjectState.StrBuilder = null;
                    return;
                }

                // Translate data into string
                builder.Append(Encoding.ASCII.GetString(buffer, 0, byteRead));

                Array.Clear(buffer, 0, byteRead);
                if (stream.DataAvailable)
                {
                    // Read until there is no more data to be transferred
                    stream.BeginRead(buffer, 0, buffer.Length, new AsyncCallback(ReadRequestCallBack), ObjectState);
                }
                else m_EventReadDone.Set();
            }
            catch (System.IO.IOException e)
            {
                if (e.InnerException is SocketException sError)
                {
                    if (sError.SocketErrorCode == SocketError.ConnectionAborted ||
                        sError.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        Logging.Log(sError.Message, LoggingLevel.Error);

                        // Client has closed connection => set String Builder to close indicating there is no more request to handle
                        ObjectState.StrBuilder = null;
                        m_EventReadDone.Set();
                        return;
                    }
                    throw sError;
                }
                throw e;
            }
        }
        private void ReadResponseCallBack(IAsyncResult ar)
        {
            // Get object state
            StreamState ObjectState = ar.AsyncState as StreamState;

            NetworkStream ServerStream = ObjectState.ReadStream;
            NetworkStream ClientStream = ObjectState.WriteStream;
            byte[] buffer = ObjectState.Buffer;

            // Get bytes read from client
            try
            {
                int byteRead = ServerStream.EndRead(ar);

                // Send data to client
                ClientStream.WriteAsync(buffer, 0, byteRead);

                Array.Clear(buffer, 0, byteRead);
                if (ServerStream.DataAvailable)
                {
                    // Read and until there is no more data to be transferred
                    ServerStream.BeginRead(buffer, 0, buffer.Length, new AsyncCallback(ReadRequestCallBack), ObjectState);
                }
            }
            catch (System.IO.IOException e)
            {
                if (e.InnerException is SocketException sError)
                {
                    if (sError.SocketErrorCode == SocketError.ConnectionAborted ||
                        sError.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        Logging.Log(sError.Message, LoggingLevel.Error);
                        return;
                    }
                    throw sError;
                }
                throw e;
            }
        }
        private IPEndPoint GetRemoteHostEndPoint(string DomainEndPoint)
        {
            if (string.IsNullOrEmpty(DomainEndPoint)) return null;
            string HostName = null, Port = null;

            int i = DomainEndPoint.IndexOf(':');
            if (i < 0)
            {
                Port = "80";
                i = DomainEndPoint.Length;
            }
            else
                Port = DomainEndPoint.Substring(DomainEndPoint.IndexOf(':') + 1);
            HostName = DomainEndPoint.Substring(0, i);
            try
            {
                return new IPEndPoint(Dns.GetHostAddresses(HostName)[1], Convert.ToInt32(Port));
            }
            catch (Exception)
            {
                return null;
            }
        }
        private string GetHostHeader(string raw_request)
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
        private string m_request = null;
        private TcpClient m_client = null;
        public HttpsClient(TcpClient tcp,string firstRequest)
        {
            m_request = firstRequest;
            m_client = tcp;
        }

        protected override bool InitInstance()
        {
            return false;

            if (string.IsNullOrEmpty(m_request) || string.IsNullOrWhiteSpace(m_request)) return false;
            if (m_client == null) return false;


            return base.InitInstance();
        }
        protected override void Run()
        {
            //
            // Receive request
            //

            //
            // Parse request
            //

            //
            // Connect and send request to remote host, redirect host's response to client
            //

            throw new NotImplementedException();
        }

        private void ReadCallBack(IAsyncResult ar)
        {

        }
    }

}
