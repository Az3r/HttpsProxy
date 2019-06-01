using System;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;

namespace ProxyTesting
{
    class Program
    {
        static void Main(string[] args)
        {
            IPEndPoint host = new IPEndPoint(new IPAddress(new byte[4] { 127, 0, 0, 1 }), 1234);
            TcpClient client = new TcpClient(host);
            Console.WriteLine("{0}", client.Connected);
            Console.ReadKey();
        }
    }
}
