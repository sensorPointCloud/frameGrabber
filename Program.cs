using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using DALSA.SaperaLT.SapClassBasic;
using Microsoft.Win32;
using System.Threading;
using System.Net.Sockets;
using System.Net;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.IO;

namespace GrabFrameExternallyTriggered
{

    class Program
    {
        SocketSrv server = null;
        Camera camera = null;
        string _save_dir = "";

        public Program()
        {
            server = new SocketSrv();
            camera = new Camera();

            string configFile = @"dalsa_config\500Hz_ex1800_gain_1_blk_1_tr_line1__debounce_100us.ccf";

            MyAcquisitionParams acqParams = new MyAcquisitionParams();
            acqParams.ConfigFileName = configFile;
            acqParams.ResourceIndex = 0;
            acqParams.ServerName = "Linea_M4096-7um_1";

            camera.InitDevice(acqParams);

            _save_dir = Program.GetDateTimeDash() + "-cam/";
            Directory.CreateDirectory(_save_dir);
            camera.SaveTo(_save_dir);

        }

        void Run()
        {
            string result;
            SapBuffer buffer = null;
            while (true)
            {
                result = server.Receive();
                while (result != "")
                {
                    //Console.WriteLine(result);
                    System.IO.File.AppendAllText(_save_dir + "MCU_data.txt", result + "\n");

                    result = server.Receive();
                }
                Thread.Sleep(2000);
            }
            server.Close();
        }

        static void Main(string[] args)
        {
            handler = new ConsoleEventDelegate(ConsoleEventCallback);
            SetConsoleCtrlHandler(handler, true);
            Program p = new Program();
            p.Run();
        }

        static bool ConsoleEventCallback(int eventType)
        {
            if (eventType == 2)
            {
                SocketSrv.KillAll();
            }
            return false;
        }
        static ConsoleEventDelegate handler;   // Keeps it from getting garbage collected
                                               // Pinvoke
        private delegate bool ConsoleEventDelegate(int eventType);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(ConsoleEventDelegate callback, bool add);

        public static string GetDateTimeDash()
        {
            var dt = DateTime.Now;
            return string.Format("{0}-{1}-{2}-{3}-{4}-{5}-{6}", dt.Year.ToString(), dt.Month.ToString(), dt.Day.ToString(), dt.Hour.ToString(), dt.Minute.ToString(), dt.Second.ToString(), dt.Millisecond.ToString());
        }

    }
    public class Camera
    {
        SapAcqDevice _acqDevice = null;
        SapBuffer _buffers = null;
        SapTransfer _transfer = null;
        SapLocation _loc = null;
        MyAcquisitionParams _acqParams = null;
        SapView _view = null;
        string _save_dir = "";

        ConcurrentQueue<SapBuffer> queue = new ConcurrentQueue<SapBuffer>();

        public void InitDevice(MyAcquisitionParams acqParams = null)
        {

            _acqParams = acqParams;

            if (acqParams == null)
            {
                string[] args = new string[0];
                if (!ExampleUtils.GetOptions(args, acqParams))
                {
                    Console.WriteLine("\nPress any key to terminate\n");
                    Console.ReadKey(true);
                    return;
                }
            }



            _loc = new SapLocation(acqParams.ServerName, acqParams.ResourceIndex);
            if (SapManager.GetResourceCount(acqParams.ServerName, SapManager.ResourceType.AcqDevice) > 0)
            {
                _acqDevice = new SapAcqDevice(_loc, acqParams.ConfigFileName);
                _buffers = new SapBufferWithTrash(2, _acqDevice, SapBuffer.MemoryType.ScatterGather);
                _transfer = new SapAcqDeviceToBuf(_acqDevice, _buffers);

                // Create acquisition object
                if (!_acqDevice.Create())
                {
                    Console.WriteLine("Error during SapAcqDevice creation!\n");
                    DestroysObjects(null, _acqDevice, _buffers, _transfer, _view);
                    return;
                }
            }

            _transfer.Pairs[0].EventType = SapXferPair.XferEventType.EndOfFrame;
            _transfer.XferNotify += new SapXferNotifyHandler(xfer_XferNotify);
            _transfer.XferNotifyContext = _buffers;

            // Create buffer object
            if (!_buffers.Create())
            {
                Console.WriteLine("Error during SapBuffer creation!\n");
                DestroysObjects(null, _acqDevice, _buffers, _transfer, _view);
                return;
            }

            // Create buffer object
            if (!_transfer.Create())
            {
                Console.WriteLine("Error during SapTransfer creation!\n");
                DestroysObjects(null, _acqDevice, _buffers, _transfer, _view);
                return;
            }

            _transfer.Grab();
        }

        public void SaveTo(string save_dir)
        {
            _save_dir = save_dir;
        }

        void xfer_XferNotify(object sender, SapXferNotifyEventArgs args)
        {
            // refresh view
            SapBuffer buffer = args.Context as SapBuffer;
            string filename = _save_dir + string.Format("Frame-{2}--Aux time-{0}--Host time-{1}.bmp", (args.AuxTimeStamp / 1e6).ToString(), (args.HostTimeStamp / 1e7).ToString(), (args.GenericParamValue0-1).ToString());
            buffer.Save(filename, "-format bmp");
            //buffer.Save("test.bmp", "-format bmp");
            Console.WriteLine("Aux time: {0}, Host time: {1}, Frame: {2}", (args.AuxTimeStamp / 1e6).ToString(), (args.HostTimeStamp / 1e7).ToString(), args.GenericParamValue0.ToString());


            SapTransfer transfer = sender as SapTransfer;
            if (transfer.UpdateFrameRateStatistics())
            {
                SapXferFrameRateInfo stats = transfer.FrameRateStatistics;
                float framerate = 0.0f;

                if (stats.IsLiveFrameRateAvailable)
                    framerate = stats.LiveFrameRate;

                // check if frame rate is stalled
                if (stats.IsLiveFrameRateStalled)
                {
                    Console.WriteLine("Live Frame rate is stalled.");
                }
            }
        }

        public SapBuffer Receive()
        {
            SapBuffer result;
            if (!queue.TryDequeue(out result))
            {
                //Console.WriteLine("CQ: TryPeek failed when it should have succeeded");
                return null;
            }
            return result;

        }


        static void DestroysObjects(SapAcquisition acq, SapAcqDevice camera, SapBuffer buf, SapTransfer xfer, SapView view)
        {

            if (xfer != null)
            {
                xfer.Destroy();
                xfer.Dispose();
            }

            if (camera != null)
            {
                camera.Destroy();
                camera.Dispose();
            }

            if (acq != null)
            {
                acq.Destroy();
                acq.Dispose();
            }

            if (buf != null)
            {
                buf.Destroy();
                buf.Dispose();
            }

            if (view != null)
            {
                view.Destroy();
                view.Dispose();
            }

            Console.WriteLine("\nPress any key to terminate\n");
            Console.ReadKey(true);
        }
    }

    class SocketSrv
    {
        bool _runServer = true;
        Thread _t;
        static List<Thread> _running_threads = new List<Thread>();
        ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        public SocketSrv()
        {
            _t = new Thread(RunServer);
            _t.Start();
            _running_threads.Add(_t);
        }

        public static void KillAll()
        {
            foreach (Thread t in _running_threads)
            {
                if(t.IsAlive)
                {
                    t.Abort();
                }
            }
        }

        void RunServer()
        {
            byte[] bytes = new Byte[1024];

            // Establish the local endpoint for the socket.  
            // Dns.GetHostName returns the name of the   
            // host running the application.  
            IPAddress ipAddress = IPAddress.Parse("169.254.39.115");
            IPEndPoint localEndPoint = new IPEndPoint(ipAddress, 1234);

            // Create a TCP/IP socket.
            Socket listener = new Socket(ipAddress.AddressFamily,
                SocketType.Stream, ProtocolType.Tcp);

            // Bind the socket to the local endpoint and   
            // listen for incoming connections.  
            try
            {
                listener.Bind(localEndPoint);
                listener.Listen(10);

                // Start listening for connections. 
                Console.WriteLine("Waiting for a connection...");
                // Program is suspended while waiting for an incoming connection.  
                Socket handler = listener.Accept();
                while (_runServer)
                {
                    string data = null;

                    // An incoming connection needs to be processed.  
                    while (true)
                    {
                        int bytesRec = handler.Receive(bytes);
                        data += Encoding.ASCII.GetString(bytes, 0, bytesRec);
                        if (data.IndexOf("\n") > -1)
                        {
                            break;
                        }
                    }

                    data = data.Replace("\n", "");
                    string time = Program.GetDateTimeDash();

                    string enque = data + "," + time;
                    queue.Enqueue(enque);
                    // Show the data on the console.  
                    //Console.WriteLine("Text enqueued : {0}", enque);
                    Console.WriteLine("Text enqueued at: {0}", time);

                    // Send data length back to the client.  
                    byte[] msg_length = Encoding.ASCII.GetBytes(data.Length.ToString() + '\n');
                    handler.Send(msg_length);

                }
                handler.Shutdown(SocketShutdown.Both);
                handler.Close();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        public string Receive()
        {
            string result;
            if (!queue.TryDequeue(out result))
            {
                return "";
            }
            return result;
        }

        public void Close()
        {
            _runServer = false;
            _t.Join();
        }
    }

}
