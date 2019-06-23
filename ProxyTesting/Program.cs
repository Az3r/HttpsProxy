using System;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net;
using System.Net.Sockets;
using System.Collections.Generic;
namespace ProxyTesting
{
    class Program
    {
        static void Main(string[] args)
        {
            List<int> c = new List<int>() { 11, 12, 13 };
            List<int> a = new List<int>() { 1, 2, 3, 4, 5 };
            List<int> b = a;
            a = c;
            Console.ReadKey();
            //            IPEndPoint host = new IPEndPoint(Dns.GetHostAddresses("example.com")[1], 443);
            //            TcpClient client = new TcpClient();
            //            client.Connect(host);
            //            /*GET / HTTP/1.1
            //Host: www.example.com*/
            //            string raw_request = "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n";
            //            byte[] SentBytes = Encoding.ASCII.GetBytes(raw_request);
            //            SslStream sslStream = new SslStream(client.GetStream());
            //            sslStream.AuthenticateAsClient("example.com");
            //            sslStream.Write(SentBytes,0, SentBytes.Length);
            //            byte[] buffer = new byte[client.ReceiveBufferSize];
            //            while(true)
            //            {
            //                int bytesRead = sslStream.Read(buffer, 0, buffer.Length);
            //                Console.Write(Encoding.UTF8.GetChars(buffer, 0, bytesRead));
            //                //for (int i = 0; i < bytesRead; i++)
            //                //    Console.Write("{0} ", buffer[i]);
            //                if (bytesRead == 0) break;
            //            }
            //            Console.ReadKey();
            //            client.Close();
        }
    }
}
