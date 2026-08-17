using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

internal static class JvdpEspOtaUploader
{
    private const int FlashCommand = 0;
    private const int AuthCommand = 200;

    private static string Md5Hex(byte[] value)
    {
        using (MD5 md5 = MD5.Create())
        {
            byte[] digest = md5.ComputeHash(value);
            StringBuilder result = new StringBuilder(digest.Length * 2);
            foreach (byte item in digest)
            {
                result.Append(item.ToString("x2"));
            }
            return result.ToString();
        }
    }

    private static string Md5Hex(string value)
    {
        return Md5Hex(Encoding.UTF8.GetBytes(value));
    }

    private static string ReceiveUdp(UdpClient client)
    {
        IPEndPoint source = null;
        return Encoding.UTF8.GetString(client.Receive(ref source));
    }

    public static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--help")
        {
            Console.WriteLine(
                "Usage: JvdpEspOtaUploader.exe <firmware.bin> [target-ip] [password or JVDP_OTA_PASSWORD]"
            );
            return 0;
        }

        string firmwarePath = args.Length > 0
            ? Path.GetFullPath(args[0])
            : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "firmware.bin");
        string targetAddress = args.Length > 1 ? args[1] : "192.168.9.1";
        string password = args.Length > 2 ? args[2] :
            Environment.GetEnvironmentVariable("JVDP_OTA_PASSWORD");

        if (String.IsNullOrWhiteSpace(password))
        {
            Console.Error.WriteLine(
                "OTA password missing. Set JVDP_OTA_PASSWORD.");
            return 2;
        }

        if (!File.Exists(firmwarePath))
        {
            Console.Error.WriteLine("Firmware not found: " + firmwarePath);
            return 1;
        }

        byte[] firmware = File.ReadAllBytes(firmwarePath);
        string firmwareMd5 = Md5Hex(firmware);
        TcpListener listener = new TcpListener(IPAddress.Any, 0);
        UdpClient udp = null;
        TcpClient device = null;

        try
        {
            listener.Start(1);
            int localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            string invitation = string.Format(
                "{0} {1} {2} {3}\n",
                FlashCommand,
                localPort,
                firmware.Length,
                firmwareMd5
            );

            udp = new UdpClient();
            udp.Client.ReceiveTimeout = 1500;
            udp.Connect(targetAddress, 3232);

            string reply = null;
            Console.Write("Contacting ESP");
            for (int attempt = 0; attempt < 10 && reply == null; attempt++)
            {
                byte[] message = Encoding.UTF8.GetBytes(invitation);
                udp.Send(message, message.Length);
                try
                {
                    reply = ReceiveUdp(udp);
                }
                catch (SocketException)
                {
                    Console.Write(".");
                }
            }
            Console.WriteLine();

            if (reply == null)
            {
                throw new IOException(
                    "No response from the ESP. Connect this PC to JvdP-LightSensor."
                );
            }

            if (reply.StartsWith("AUTH ", StringComparison.Ordinal))
            {
                string nonce = reply.Substring(5).Trim();
                string cnonce = Md5Hex(Guid.NewGuid().ToString("N"));
                string passwordMd5 = Md5Hex(password);
                string response = Md5Hex(
                    passwordMd5 + ":" + nonce + ":" + cnonce
                );
                string authMessage = string.Format(
                    "{0} {1} {2}\n",
                    AuthCommand,
                    cnonce,
                    response
                );
                byte[] authBytes = Encoding.UTF8.GetBytes(authMessage);
                udp.Send(authBytes, authBytes.Length);
                udp.Client.ReceiveTimeout = 10000;
                reply = ReceiveUdp(udp);
            }

            if (reply.Trim() != "OK")
            {
                throw new UnauthorizedAccessException(
                    "ESP rejected the OTA request: " + reply.Trim()
                );
            }

            IAsyncResult pending = listener.BeginAcceptTcpClient(null, null);
            if (!pending.AsyncWaitHandle.WaitOne(10000))
            {
                throw new TimeoutException(
                    "ESP did not open the firmware transfer connection."
                );
            }
            device = listener.EndAcceptTcpClient(pending);
            device.SendTimeout = 10000;
            device.ReceiveTimeout = 10000;

            using (NetworkStream stream = device.GetStream())
            {
                byte[] responseBuffer = new byte[32];
                int offset = 0;
                while (offset < firmware.Length)
                {
                    int length = Math.Min(1024, firmware.Length - offset);
                    stream.Write(firmware, offset, length);
                    stream.Flush();

                    int responseLength = stream.Read(
                        responseBuffer, 0, responseBuffer.Length
                    );
                    string chunkReply = Encoding.UTF8.GetString(
                        responseBuffer, 0, responseLength
                    );
                    if (chunkReply.IndexOf("OK", StringComparison.Ordinal) < 0)
                    {
                        throw new IOException(
                            "ESP rejected a firmware block: " + chunkReply.Trim()
                        );
                    }

                    offset += length;
                    int percentage = (int)((long)offset * 100 / firmware.Length);
                    Console.Write("\rUploading: {0,3}%", percentage);
                }
            }

            Console.WriteLine();
            Console.WriteLine("OTA update completed. The ESP is restarting.");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine();
            Console.Error.WriteLine("OTA update failed: " + error.Message);
            return 1;
        }
        finally
        {
            if (device != null)
            {
                device.Close();
            }
            if (udp != null)
            {
                udp.Close();
            }
            listener.Stop();
        }
    }
}
