using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Net.Sockets;
using System.Net;
using System;
using System.Threading;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace MyWasapi
{
    class Program
    {
        static void Main(string[] args)
        {
            /*-b"169.254.1.99"     接收的binding ip
              -m"239.255.255.31"   接收的Multicast ip
              -p"5005"             接收的Multicast port
              -w"48000,2,16"       PCM參數
              -o"Realtek xxx"      輸出裝置
              -tb"192.168.1.180"   發送的binding ip
              -tm"239.255.255.31"  發送的Multicast ip
              -tu"169.254.1.1"     發送的UDP ip 
              -tp"5005"            發送的Multicast port
              -i"USB AUDIO CODEC"  輸入裝置
              -l                   列出所有裝置
              -lb"Realtek xxx"     錄製loopback裝置
              -f"xxx.mp3"          播放音檔
              -v"100"              音檔音量
              -a"10"               區段播放，開始秒數
              -ab"30"              區段播放，結束秒數*/

            var CurrentDirectory = Directory.GetCurrentDirectory();
            Console.WriteLine(CurrentDirectory);

            MyRecord aRecord = new MyRecord(args);

            //非同步初始化
            Task t = aRecord.Init();

            BlockingCollection<string> inputs = new BlockingCollection<string>();

            Task t2 = Task.Run(() =>
            {
                foreach (string str in inputs.GetConsumingEnumerable())
                {
                    if (str == "p")
                    {
                        aRecord.PausePlay();
                    }
                    else if (str == "r")
                    {
                        aRecord.ResumePlay();
                    }
                    else if (str == "s")
                    {
                        aRecord.StopPlay();
                    }
                }
            });

            while (true)
            {
                string str = Console.ReadLine();
                inputs.Add(str);
            }
        }          
    }

    public class MyRecord
    {
        private string recvBinding = "";
        private string recvMcast = "";
        private int recvPort = 5005;
        private int bitRate = 48000;
        private int ch = 2;
        private int bit = 16;
        private string outDev = "";
        private string sendBinding = "";
        private string sendMCast = "";
        private string sendUdp = "";
        private int sendPort = 5005;
        private string inDev = "";
        private string lbDev = "";
        private string fileName = "";
        private int vol = 100;
        private int a = 0;
        private int b = 0;

        private MMDevice capDev, playDev, lbCapDev;
        private WasapiCapture capture;
        private WasapiLoopbackCapture lbCapture;
        private WasapiOut playback;
        private BufferedWaveProvider sound;
        private AudioFileReader fileReader;

        private UdpClient client;
        private IPEndPoint sendIp;

        private bool isLoopBackCap;

        public MyRecord(string[] args)
        {
            bool isList = false;

            if (args.Length > 0)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string temp = args[i].ToString();

                    //2碼的先比對
                    if (temp.StartsWith("-lb"))
                        lbDev = temp.Substring(3);
                    else if (temp.StartsWith("-tb"))
                        sendBinding = temp.Substring(3);
                    else if (temp.StartsWith("-tm"))
                        sendMCast = temp.Substring(3);
                    else if (temp.StartsWith("-tu"))
                        sendUdp = temp.Substring(3);
                    else if (temp.StartsWith("-tp"))
                    {
                        if (int.TryParse(temp.Substring(3), out int vSendPort))
                            sendPort = vSendPort;
                    }
                    else if (temp.StartsWith("-ab"))
                    {
                        if (int.TryParse(temp.Substring(3), out int vB))
                            b = vB;
                    }
                    else if (temp.StartsWith("-l"))
                        isList = true;
                    else if (temp.StartsWith("-b"))
                        recvBinding = temp.Substring(2);
                    else if (temp.StartsWith("-m"))
                        recvMcast = temp.Substring(2);
                    else if (temp.StartsWith("-p"))
                    {
                        if (int.TryParse(temp.Substring(2), out int vRecvPort))
                            recvPort = vRecvPort;
                    }
                    else if (temp.StartsWith("-w"))
                    {
                        string[] wArr = temp.Substring(2).Split(',');
                        if (wArr.Length == 3)
                        {
                            if (int.TryParse(wArr[0], out int vBitRate))
                                bitRate = vBitRate;
                            if (int.TryParse(wArr[1], out int vCh))
                                ch = vCh;
                            if (int.TryParse(wArr[2], out int vBit))
                                bit = vBit;
                        }
                    }
                    else if (temp.StartsWith("-o"))
                        outDev = temp.Substring(2);
                    else if (temp.StartsWith("-i"))
                        inDev = temp.Substring(2);
                    else if (temp.StartsWith("-f"))
                        fileName = temp.Substring(2);
                    else if (temp.StartsWith("-v"))
                    {
                        if (int.TryParse(temp.Substring(2), out int vVol))
                            vol = vVol;

                        if (vol > 100)
                            vol = 100;
                        else if (vol < 1)
                            vol = 1;
                    }
                    else if (temp.StartsWith("-a"))
                    {
                        if (int.TryParse(temp.Substring(2), out int vA))
                            a = vA;
                    }
                }
            }

            var enumerator = new MMDeviceEnumerator();
            foreach (var wasapi in enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.Active))
            {
                if (isList)
                    Console.WriteLine($"{wasapi.DataFlow} {wasapi.FriendlyName} {wasapi.DeviceFriendlyName} {wasapi.State}");

                if (outDev != "" && playDev == null)
                {
                    if (outDev.ToLower() == "default")
                    {
                        try
                        {
                            //有可能沒有預設裝置
                            playDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        }
                        catch { playDev = null; }
                    }
                    else if (wasapi.DataFlow == DataFlow.Render && wasapi.FriendlyName.StartsWith(outDev))
                    {
                        playDev = wasapi;
                        Console.WriteLine($"outDev = {wasapi.FriendlyName}");
                    }
                }

                if (inDev != "" && capDev == null)
                {
                    if (inDev.ToLower() == "default")
                    {
                        try
                        {
                            //有可能沒有預設裝置
                            capDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                        }
                        catch { capDev = null; }
                    }
                    else if (wasapi.DataFlow == DataFlow.Capture && wasapi.FriendlyName.StartsWith(inDev))
                    {
                        capDev = wasapi;
                        Console.WriteLine($"inDev = {wasapi.FriendlyName}");
                    }
                }

                if (lbDev != "" && lbCapDev == null)
                {
                    if (lbDev.ToLower() == "default")
                    {
                        try
                        {
                            //有可能沒有預設裝置
                            capDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        }
                        catch { capDev = null; }
                    }
                    else if (wasapi.DataFlow == DataFlow.Render && wasapi.FriendlyName.StartsWith(lbDev))
                    {
                        lbCapDev = wasapi;
                        Console.WriteLine($"lbDev = {wasapi.FriendlyName}");
                    }
                }
            }

            if (isList)
            {
                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    if (dev != null)
                    {
                        Console.WriteLine($"default multimedia = {dev.FriendlyName}");
                    }
                }
                catch { }

                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                    if (dev != null)
                    {
                        Console.WriteLine($"default Console = {dev.FriendlyName}");
                    }
                }
                catch { }

                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                    if (dev != null)
                    {
                        Console.WriteLine($"default Communications = {dev.FriendlyName}");
                    }
                }
                catch { }

                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
                    if (dev != null)
                    {
                        Console.WriteLine($"default multimedia = {dev.FriendlyName}");
                    }
                }
                catch { }

                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
                    if (dev != null)
                    {
                        Console.WriteLine($"default Console = {dev.FriendlyName}");
                    }
                }
                catch { }

                try
                {
                    MMDevice dev = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                    if (dev != null)
                    {
                        Console.WriteLine($"default Communications = {dev.FriendlyName}");
                    }
                }
                catch { }
            }

            if (isList)
                Environment.Exit(0);

            if (outDev != "" && playDev == null)
                Environment.Exit(0);

            if (inDev != "" && capDev == null)
                Environment.Exit(0);

            //Init();            
        }

        public async Task Init()
        {
            WaveFormat wft = new WaveFormat(bitRate, bit, ch);
            sound = new BufferedWaveProvider(wft);

            if (outDev != "")
            {
                playback = new WasapiOut(playDev, AudioClientShareMode.Shared, false, 0);                
            }   

            if (fileName != "")
            {
                if (!File.Exists(fileName))
                {
                    Console.WriteLine($"file not found");
                    Environment.Exit(0);
                }

                fileReader = new AudioFileReader(fileName);
                fileReader.Volume = vol * 0.01f;
                                
                playback.Init(fileReader);

                //加入播放結束事件
                playback.PlaybackStopped += (sender, e) =>
                {
                    Console.WriteLine("playback stopped");
                    Environment.Exit(0);
                };

                //set ab play
                if (a > 0)
                {
                    fileReader.CurrentTime = TimeSpan.FromSeconds(a);                    
                }              

                playback.Play();

                while (playback.PlaybackState != PlaybackState.Stopped)
                {
                    try
                    {
                        if (playback.PlaybackState == PlaybackState.Playing)
                        {
                            //播放進度
                            var pos = fileReader.CurrentTime;
                            Console.WriteLine($"{pos.ToString(@"hh\:mm\:ss")}");

                            if (b > 0 && pos.TotalSeconds >= b)
                            {
                                playback.Stop();
                                Environment.Exit(0);
                            }
                        }                        
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                        Environment.Exit(0);
                    }

                    await Task.Delay(1000); 
                    await Task.Yield();
                }

                Environment.Exit(0);
            }
            else
            {
                playback.Init(sound);

                if (inDev != "")
                {
                    isLoopBackCap = false;
                    capture = new WasapiCapture(capDev);
                    capture.ShareMode = AudioClientShareMode.Shared;
                    capture.DataAvailable += capture_DataAvailable;
                    capture.RecordingStopped += capture_RecordingStopped;
                    capture.WaveFormat = new WaveFormat(bitRate, bit, ch);

                    StartRecording();
                }
                else if (lbDev != "")
                {
                    isLoopBackCap = true;
                    lbCapture = new WasapiLoopbackCapture(lbCapDev);
                    lbCapture.ShareMode = AudioClientShareMode.Shared;
                    lbCapture.DataAvailable += lbCapture_DataAvailable;
                    lbCapture.RecordingStopped += lbCapture_RecordingStopped;
                    lbCapture.WaveFormat = new WaveFormat(bitRate, bit, ch);

                    StartRecording();
                }
            }            
            
            if (recvBinding != "")
            {
                Thread t = new Thread(new ThreadStart(RecvThread));
                t.IsBackground = true;
                t.Start();
            }

            if (sendBinding != "")
            {
                if (sendMCast != "")
                {
                    IPEndPoint ipend = new IPEndPoint(IPAddress.Parse(sendBinding), 0);
                    client = new UdpClient(ipend);
                    client.JoinMulticastGroup(IPAddress.Parse(sendMCast));
                    sendIp = new IPEndPoint(IPAddress.Parse(sendMCast), sendPort);
                }
                else if (sendUdp != "")
                {
                    IPEndPoint ipend = new IPEndPoint(IPAddress.Parse(sendBinding), 0);
                    client = new UdpClient(ipend);
                    sendIp = new IPEndPoint(IPAddress.Parse(sendUdp), sendPort);
                }
            }          
        }

        void capture_DataAvailable(object sender, WaveInEventArgs e)
        {
            Console.WriteLine($"cap: {e.BytesRecorded}");

            //本地錄音，串流優先
            if ( (sendMCast != "" || sendUdp != "") && sendBinding != "")
            {
                try
                {
                    client.Send(e.Buffer, e.BytesRecorded, sendIp);
                }
                catch
                {
                    Console.WriteLine($"send error");
                }
            }
            else if (outDev != "")
            {
                try
                {
                    sound.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    playback.Play();
                }
                catch
                {
                    Console.WriteLine($"playback error");
                }
            }
        }

        void capture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            capture.Dispose();
        }

        void lbCapture_DataAvailable(object sender, WaveInEventArgs e)
        {
            Console.WriteLine($"lbCap: {e.BytesRecorded}");

            //本地錄音，串流優先
            if ( (sendMCast != "" || sendUdp != "") && sendBinding != "")
            {
                try
                {
                    client.Send(e.Buffer, e.BytesRecorded, sendIp);
                }
                catch
                {
                    Console.WriteLine($"send error");
                }
            }
            else if (outDev != "")
            {
                try
                {
                    sound.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    playback.Play();
                }
                catch
                {
                    Console.WriteLine($"playback error");
                }
            }
        }

        void lbCapture_RecordingStopped(object sender, StoppedEventArgs e)
        {
            lbCapture.Dispose();
        }

        void RecvThread()
        {
            try
            {
                UdpClient client;

                if (recvMcast != "") 
                {
                    client = new UdpClient(recvPort);
                    client.JoinMulticastGroup(IPAddress.Parse(recvMcast), IPAddress.Parse(recvBinding));
                }
                else
                {
                    IPEndPoint ipend = new IPEndPoint(IPAddress.Parse(recvBinding), recvPort);
                    client = new UdpClient(ipend);
                }                                
                
                IPEndPoint mult = null;
            
                while (true)
                {
                    try 
                    { 
                        byte[] buf = client.Receive(ref mult);
                        Console.WriteLine($"recv: {buf.Length}");

                        //接收串流，本地輸出優先
                        if (outDev != "")
                        {
                            try
                            {
                                sound.AddSamples(buf, 0, buf.Length);
                                playback.Play();
                            }
                            catch
                            {
                                Console.WriteLine($"playback error");
                            }
                        }
                        else if ( (sendMCast != "" || sendUdp != "") && sendBinding != "")
                        {
                            try
                            {
                                client.Send(buf, buf.Length, sendIp);
                            }
                            catch
                            {
                                Console.WriteLine($"send error1");
                            }
                        }
                    }
                    catch 
                    {
                        Console.WriteLine($"recv error2");
                    }
                }
            }
            catch 
            {
                Console.WriteLine($"recv error3");
            }            
        }

        void StartRecording()
        {
            if (isLoopBackCap)
                lbCapture.StartRecording();
            else
                capture.StartRecording();
        }

        void StopRecording()
        {
            if (isLoopBackCap)
                lbCapture.StopRecording();
            else
                capture.StopRecording();
        }

        public void PausePlay()
        {
            if (playback != null)
                playback.Pause();
        }

        public void ResumePlay()
        {
            if (playback != null)
                playback.Play();
        }

        public void StopPlay()
        {
            if (playback != null)
                playback.Stop();
        }
    }  
}