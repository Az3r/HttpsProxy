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
            IPEndPoint host = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1234);
            TcpClient client = new TcpClient();
            client.Connect(host);
            /*GET / HTTP/1.1
Host: www.example.com*/
            string raw_request = "GET / HTTP/1.1\r\nHost: www.example.com\r\n\r\n";
            byte[] SentBytes = Encoding.ASCII.GetBytes(raw_request);
            client.GetStream().Write(SentBytes,0, SentBytes.Length);
            Thread.Sleep(500);
            NetworkStream ReadStream = client.GetStream();
            byte[] buffer = new byte[client.ReceiveBufferSize];
            while(ReadStream.DataAvailable)
            {
                int bytesRead = ReadStream.Read(buffer, 0, buffer.Length);
                Console.Write(Encoding.ASCII.GetChars(buffer, 0, bytesRead));
            }
            Console.ReadKey();
            client.Close();
        }
    }
}
