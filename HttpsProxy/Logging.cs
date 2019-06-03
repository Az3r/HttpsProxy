using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace HttpsProxy
{
    public enum LoggingLevel { Info = 0, Warning = 1, Error = 2 };
    public class Logging
    {
        static private AutoResetEvent eventDoLogging = null;
        static private Logging Instance = null;
        static private Thread m_thread = null;

        struct Message
        {
            public string message;
            public LoggingLevel level;
        }

        static private Queue<Message> MsgQueue = new Queue<Message>();

        private Logging()
        {
            eventDoLogging = new AutoResetEvent(false);
            m_thread = new Thread(DoLogging)
            {
                IsBackground = false,
                Name = "Logging Messages",
                Priority = ThreadPriority.BelowNormal
            };
        }
        static public Logging CreateLogger()
        {
            if(Instance == null)
            {
                Instance = new Logging();
                m_thread.Start();
            }
            return Instance;
        }
        static public void Log(string msg, LoggingLevel level = LoggingLevel.Info)
        {
            if (string.IsNullOrEmpty(msg) || Instance == null) return;
            lock (MsgQueue)
            {
                MsgQueue.Enqueue(new Message() { level = level, message = msg });
            }
            eventDoLogging.Set();
        }
        static private void DoLogging()
        {
            while(!AppSignal.Exit)
            {
                while (MsgQueue.Count > 0)
                {
                    Message msg = MsgQueue.Dequeue();
                    if (string.IsNullOrEmpty(msg.message)) return;
                    switch (msg.level)
                    {
                        case LoggingLevel.Info:
                            Console.ForegroundColor = ConsoleColor.White;
                            break;
                        case LoggingLevel.Warning:
                            Console.ForegroundColor = ConsoleColor.Yellow;
                            break;
                        case LoggingLevel.Error:
                            Console.ForegroundColor = ConsoleColor.Red;
                            break;
                        default:
                            throw new ArgumentException();
                    }
                    Console.WriteLine(msg.message);
                }
                Console.ResetColor();
                eventDoLogging.WaitOne();
            }
        }
    }
}