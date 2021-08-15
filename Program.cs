using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GrandstreamATAConfigurator
{
    internal static class Program
    {
        private static string _ip = "";

        private const string Username = "admin";
        private static string _password = "admin";
        private static string _ataPassword = "admin";
        private static string _sipPassword = "";
        private const string TimeZone = "EST5EDT";
        private static string _primaryServer = "";
        private static string _failoverServer = "";
        private const int Timeout = 4;
        private static bool _reset;

        private static string _phoneNumber;

        private static void Main()
        {
            string title = "SLE's Grandstream SSH Setup";
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine(title);
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine();

            _ip = GetLocalIPv4(NetworkInterfaceType.Wireless80211);

            Console.WriteLine("Now scanning your network for a Grandstream device...");
            if (PortScan())
                Console.WriteLine("Grandstream device found! Using IP: " + _ip);
            else
            {
                Console.Write("Oops, we can't find a Grandstream device on this network. Make " +
                              "sure you're connected to the right network, then try again.");
                return;
            }

            using var client = new SshClient(_ip, Username, _password);
            Console.WriteLine("Attempting connection...");
            try
            {
                client.Connect();
                client.Disconnect();
            }
            catch (SshAuthenticationException)
            {
                for (var i = 0; i < 3; i++)
                {
                    Console.Write(i == 0
                        ? "Please enter the password for the ATA. This may be a customer number: "
                        : "Hmm, that password didn't work. Try again: ");
                    _password = Console.ReadLine();
                    try
                    {
                        client.Connect();
                        client.Disconnect();
                        break;
                    }
                    catch
                    {
                        // ignored
                    }
                }

                Console.WriteLine();
                Console.WriteLine("======================================================");
                Console.WriteLine("Looks like the password check failed three times.");
                Console.WriteLine("Best thing to do from here is factory reset your ATA.");
                Console.WriteLine("Using a pin, paperclip, etc. press and hold the reset button on your ATA");
                Console.WriteLine("until the lights turn off. Once the globe light turns on, try again.");
                Console.WriteLine("======================================================");
                Console.WriteLine();
                Console.WriteLine("Press any key to close.");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("OK!");
            for (var i = 0; i < 3; i++)
            {
                Console.Write(".");
                Thread.Sleep(1000);
            }

            GetParams();

            ResetOrConfigureAta(_reset);
        }

        private static bool PortScan()
        {
            for (var i = 1; i < 255; i++)
            {
                var bytes = IPAddress.Parse(_ip).GetAddressBytes();
                bytes[3] = (byte) i;
                IPAddress newIp = new(bytes);

                foreach (var s in Ports)
                {
                    using var scan = new TcpClient();
                    try
                    {
                        if (scan.ConnectAsync(newIp, s).Wait(20))
                        {
                            var macAddress = GetMacByIp(newIp.ToString());
                            Console.WriteLine($"{newIp}[{s}] | FOUND, MAC: {macAddress}", Color.Green);
                            if (IsGrandstream(macAddress))
                            {
                                _ip = newIp.ToString();
                                return true;
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine("Whoops, couldn't get that... " + newIp);
                        Console.WriteLine(e);
                        throw;
                    }
                }
            }

            return false;
        }

        private static readonly int[] Ports = new int[]
        {
            80,
            443
        };

        private static string GetMacByIp(string ip)
        {
            var pairs = GetMacIpPairs();

            foreach (var pair in pairs)
            {
                if (pair.IpAddress == ip)
                    return pair.MacAddress;
            }

            return "NOT FOUND";
        }

        private static IEnumerable<MacIpPair> GetMacIpPairs()
        {
            var pProcess = new System.Diagnostics.Process
            {
                StartInfo =
                {
                    FileName = "arp",
                    Arguments = "-a ",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            pProcess.Start();

            var cmdOutput = pProcess.StandardOutput.ReadToEnd();
            const string pattern = @"(?<ip>([0-9]{1,3}\.?){4})\s*(?<mac>([a-f0-9]{2}-?){6})";

            foreach (Match m in Regex.Matches(cmdOutput, pattern, RegexOptions.IgnoreCase))
            {
                yield return new MacIpPair()
                {
                    MacAddress = m.Groups["mac"].Value,
                    IpAddress = m.Groups["ip"].Value
                };
            }
        }

        private struct MacIpPair
        {
            public string MacAddress;
            public string IpAddress;
        }

        private static bool IsGrandstream(string mac)
        {
            const string pattern = "^([Cc][0][-:][7][4][-:][Aa][Dd][:-])([0-9A-Fa-f]{2}[:-]){2}([0-9A-Fa-f]{2})$";
            return Regex.IsMatch(mac, pattern);
        }

        private static string GetLocalIPv4(NetworkInterfaceType type)
        {
            var output = "";
            foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.NetworkInterfaceType != type || item.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ip in item.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        output = ip.Address.ToString();
                    }
                }
            }

            return output;
        }

        private static void GetParams()
        {
            var done = false;
            while (!done)
            {
                Console.Clear();
                Console.WriteLine("We're now going to get some info from you.");
                Console.WriteLine();
                VerifyPhone();
                Console.WriteLine();
                Console.Write("And what's your SIP password? If you don't know this, contact your provider: ");
                _sipPassword = Console.ReadLine();
                while (true)
                {
                    Console.WriteLine();
                    Console.Write("Your primary server? ");
                    _primaryServer = Console.ReadLine();
                    if (_primaryServer == String.Empty)
                        Console.WriteLine("Primary server cannot be empty...");
                    else
                        break;
                }

                Console.WriteLine();
                Console.Write("Your failover server? (Optional): ");
                _failoverServer = Console.ReadLine();
                while (true)
                {
                    Console.WriteLine();
                    Console.Write("What should the new ATA password be? ");
                    _ataPassword = Console.ReadLine();
                    if (_ataPassword == String.Empty)
                        Console.WriteLine("ATA password cannot be empty...");
                    else
                        break;
                }

                Console.Clear();

                new Action(() =>
                {
                    while (true)
                    {
                        Console.Write("Are we resetting the ATA first? [Y/n]");
                        var reset = Console.ReadKey().Key;
                        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                        switch (reset)
                        {
                            case ConsoleKey.Enter:
                            case ConsoleKey.Y:
                                _reset = true;
                                return;
                            case ConsoleKey.N:
                                _reset = false;
                                return;
                        }

                        Console.WriteLine("Sorry, that wasn't a valid input.");
                    }
                })();

                new Action(() =>
                {
                    while (true)
                    {
                        Console.Clear();
                        Console.WriteLine("Current password: " + _password);
                        Console.WriteLine("New password: " + _ataPassword);
                        Console.WriteLine("VoIP phone number to add: " + _phoneNumber);
                        Console.WriteLine("SIP password: " + _sipPassword);
                        Console.WriteLine("Primary server: " + _primaryServer);
                        Console.WriteLine("Failover server: " + _failoverServer);
                        Console.WriteLine("Resetting the ATA first: " + _reset);
                        Console.WriteLine();
                        Console.Write("Is all of the above correct? ");
                        var good = Console.ReadKey().Key;
                        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                        switch (good)
                        {
                            case ConsoleKey.Enter:
                            case ConsoleKey.Y:
                                done = true;
                                return;
                            case ConsoleKey.N:
                                return;
                        }

                        Console.WriteLine("Sorry, that wasn't a valid input.");
                        Thread.Sleep(3000);
                    }
                })();
            }
        }

        private static void VerifyPhone()
        {
            while (true)
            {
                Console.Write("What's the phone number you wish to assign to this adapter? ");
                _phoneNumber = Console.ReadLine();
                _phoneNumber = new string(
                    (_phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
                if (_phoneNumber[0] == '1')
                    _phoneNumber = _phoneNumber.Remove(0, 1);
                if (_phoneNumber.Length == 10)
                    return;
                Console.WriteLine("Sorry, that's not a valid phone number. Please try again.");
            }
        }

        private static void ResetOrConfigureAta(bool reset)
        {
            while (true)
            {
                Console.WriteLine();
                var warning = "We will now reset the ATA. Do NOT touch anything during this process.";
                var commands = new[] {"reset 0", "y"};
                using var client = new SshClient(_ip, Username, _password);


                if (!reset)
                {
                    warning = "We will now configure the ATA. Do NOT touch anything during this process.";

                    commands = new[]
                    {
                        "config", "set 196 " + _ataPassword, "set 276 0", "set 64 " + TimeZone, "set 2 " + _ataPassword,
                        "set 88 0", "set 277 1", "set 47 " + _primaryServer, "set 967 " + _failoverServer, "set 52 2",
                        "set 35 " + _phoneNumber, "set 36 " + _phoneNumber, "set 34 " + _sipPassword, "set 109 0",
                        "set 20501 1", "set 20505 5", "set 288 1", "set 243 1", "set 2339 0", "set 850 101",
                        "set 851 100", "set 852 102", "set 191 0", "set 85 " + Timeout, "set 29 0", "set 57 0",
                        "set 58 18", "set 59 0", "set 60 0", "set 61 0", "set 62 0", "set 63 0", "commit", "exit",
                        "reboot"
                    };
                }

                Console.WriteLine(new string('=', warning.Length));
                Console.WriteLine(warning);
                Console.WriteLine(new string('=', warning.Length));
                Console.WriteLine();

                client.Connect();
                using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

                foreach (var command in commands)
                {
                    sshStream.WriteLine(command);
                    // uncomment this to see what's being sent/received
                    // string line;
                    // while((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(200))) != null)
                    //     Console.WriteLine(line);
                    Thread.Sleep(100);
                }

                sshStream.Close();
                client.Disconnect();

                Thread.Sleep(30000);
                for (var i = 0; i < 60; i++)
                {
                    try
                    {
                        client.Connect();
                        break;
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(2000);
                    }
                }

                Console.Clear();
                Console.Write(reset
                    ? "ATA successfully reset, press any key to continue..."
                    : "ATA successfully configured, press any key to exit...");
                Console.ReadKey();
                if (reset)
                {
                    reset = false;
                    continue;
                }

                Console.WriteLine("Have a nice day (ﾉ◕ヮ◕)ﾉ*:･ﾟ✧");
                break;
            }
        }
    }
}