using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
namespace HttpsProxy
{
    class Program
    {
        static void Main(string[] args)
        {
            ProxyListener proxy = new ProxyListener(1234);
            proxy.Start("Listener");
            Console.ReadKey();
        }
    }

    public abstract class Worker
    {
        private Thread m_worker = null;
        public virtual bool Start(string Name = "Worker",bool IsBackground = true)
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
        protected virtual int ExitInstance() { return 0; }
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
        private int m_port = 0;
        public ProxyListener(int port)
        {
            m_port = port;
        }
        protected override bool InitInstance()
        {
            m_tcpListener = new TcpListener(IPAddress.Any, m_port);
            m_tcpListener.Start();
            return true;
        }
        protected override void Run()
        {
            while(true)
            {
                // Accepting pending connection
                TcpClient m_tcpClient = m_tcpListener.AcceptTcpClient();

                // Attach to a new thread which determines whether it is an http connection or https connection
                ProtocolDistributor distributor = new ProtocolDistributor(m_tcpClient);
                distributor.Start("ProtocolDistributor");
            }
        }
    }
    public sealed class ProtocolDistributor : Worker
    {
        private TcpClient m_tcpSocket = null;
        private byte[] m_buffer = null;
        ManualResetEvent eventFinishReading = null;

        private class RequestHeader
        {
           public string Method { get; set; } = null;
           public string Domain { get; set; } = null;
           public string Port { get; set; } = null;
        }

        static private RequestHeader ParseRequest(string raw_request)
        {
            try
            {
                if (string.IsNullOrEmpty(raw_request)) return null;
                if (string.IsNullOrWhiteSpace(raw_request)) return null;

                RequestHeader header = new RequestHeader();
                header.Method = raw_request.Substring(0, raw_request.IndexOf(' '));

                int HostStart = raw_request.IndexOf("Host: ");
                int HostEnd = raw_request.IndexOfAny(new char[] {'\r', '\n' }, HostStart);

                string Host = raw_request.Substring(HostStart + 6, HostEnd - HostStart);
                try
                {
                    int i = Host.IndexOf(':');
                    header.Domain = Host.Substring(0, i);
                    header.Port = Host.Substring(i + 1, Host.IndexOfAny(new char[] { '\r', '\n' }) - i - 1);
                }
                catch (ArgumentOutOfRangeException)
                {
                    header.Domain = Host;
                    header.Port = "80";
                }
                return header;

            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        public ProtocolDistributor(TcpClient tcp)
        {
            m_tcpSocket = tcp;
        }

        protected override bool InitInstance()
        {
            try
            {
                eventFinishReading = new ManualResetEvent(false);

                //m_buffer = new byte[50];
                m_buffer = new byte[m_tcpSocket.ReceiveBufferSize];

                return base.InitInstance();
            }
            catch (Exception)
            {
                return false;
            }
        }
        protected override void Run()
        {
            if (m_tcpSocket == null) return;

            // Read bytes and traslate client's request into string
            object[] ObjectState = new object[2] { m_tcpSocket.GetStream(), new StringBuilder() };

            IAsyncResult Result = m_tcpSocket.GetStream().BeginRead(m_buffer, 0, m_buffer.Length, new AsyncCallback(ReadCallBack), ObjectState);

            // Wait until finishing reading client's request
            eventFinishReading.WaitOne();
            RequestHeader header = ParseRequest(ObjectState[1].ToString());
            if (header != null)
                Console.WriteLine("Method: {0}\nHost: {1}:{2}\n", header.Method, header.Domain, header.Port);
        }

        private void ReadCallBack(IAsyncResult ar)
        {
            //Console.WriteLine("Thead ID {0} is called", Thread.CurrentThread.ManagedThreadId);

            // Get object state
            object[] ObjectState = (object[])ar.AsyncState;

            NetworkStream stream = ObjectState[0] as NetworkStream;
            StringBuilder builder = ObjectState[1] as StringBuilder;

            // Get bytes read from client
            int byteRead = stream.EndRead(ar);

            // Translate data into string
            builder.Append(Encoding.ASCII.GetString(m_buffer, 0, byteRead));

            Array.Clear(m_buffer, 0, byteRead);
            if (stream.DataAvailable)
            {
                // Read until there is no more data to be transferred
                stream.BeginRead(m_buffer, 0, m_buffer.Length, new AsyncCallback(ReadCallBack), ObjectState);
            }
            else eventFinishReading.Set();

            //Console.WriteLine("Thead ID {0} exits", Thread.CurrentThread.ManagedThreadId);
        }
    }

}
