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
        // for locating, connecting to ATA
        private static string _ip = "";
        
        // global flag for factory resetting ATA
        private static bool _reset;
        
        // Grandstream config variables
        private static string _adminPassword = "admin";
        private static string _authenticatePassword = "";
        private static string _failoverServer = "";
        private const int NoKeyTimeout = 4;
        private static string _password = "admin";
        private static string _phoneNumber;
        private static string _primaryServer = "";
        private const string TimeZone = "EST5EDT";
        private const string Username = "admin";
        
        private static readonly int[] Ports = new[]
        {
            80,
            443
        };

        private static readonly int[] Gateways = new[]
        {
            0,
            1,
            50,
            254
        };

        // MAIN
        private static void Main()
        {
            // print title card
            const string title = "SLE's Grandstream SSH Setup";
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine(title);
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine();
            
            _ip = GetLocalIPv4(GetInterface().NetworkInterfaceType);

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
                // just try connecting using default credentials, if they work awesome, if not prompt
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
        
        // extensions of main (for readability)
        
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
                _authenticatePassword = Console.ReadLine();
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
                    _adminPassword = Console.ReadLine();
                    if (_adminPassword == String.Empty)
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
                        Console.WriteLine("New password: " + _adminPassword);
                        Console.WriteLine("VoIP phone number to add: " + _phoneNumber);
                        Console.WriteLine("SIP password: " + _authenticatePassword);
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
                    (_phoneNumber ?? string.Empty).Where(char.IsDigit).ToArray()); // strips any non-numeric characters
                if (_phoneNumber.Length == 11)
                    return;
                Console.WriteLine("Sorry, that's not a valid phone number. Please try again.");
            }
        }

        private static void ResetOrConfigureAta(bool reset)
        {
            while (true)
            {
                Console.WriteLine();
                using var client = new SshClient(_ip, Username, _password);
                string warning;
                string[] commands;


                if (reset)
                {
                    warning = "We will now reset the ATA. Do NOT touch anything during this process.";
                    commands = new[] {"reset 0", "y"};
                }
                else
                {
                    warning = "We will now configure the ATA. Do NOT touch anything during this process.";

                    commands = new[]
                    {
                        "config",
                        "set 196 " + _adminPassword, // end user password
                        "set 276 0", // telnet
                        "set 64 " + TimeZone, // time zone
                        "set 2 " + _adminPassword, // admin password
                        "set 88 0", // lock keypad update
                        "set 277 1", // disable direct IP call
                        "set 47 " + _primaryServer, // primary server
                        "set 967 " + _failoverServer, // failover server
                        "set 52 2", // NAT Traversal
                        "set 35 " + _phoneNumber, // user ID
                        "set 36 " + _phoneNumber, // authenticate ID
                        "set 34 " + _authenticatePassword, // authenticate password
                        "set 109 0", // outgoing call without registration
                        "set 20501 1", // random SIP port
                        "set 20505 5", // random RTP port
                        "set 288 1", // support SIP instance ID
                        "set 243 1", // SIP proxy only
                        "set 2339 0", // Use P-Preferred-Identity-Header
                        // DTMF
                        "set 850 101",
                        "set 851 100",
                        "set 852 102",
                        // end DTMF
                        "set 191 0", // call features
                        "set 85 " + NoKeyTimeout, // no key timeout
                        "set 29 0", // early dial
                        // vocoder 1-7
                        "set 57 0",
                        "set 58 18",
                        "set 59 0",
                        "set 60 0",
                        "set 61 0",
                        "set 62 0",
                        "set 63 0",
                        // save changes
                        "commit",
                        // navigate back to main menu
                        "exit",
                        // reboot
                        "reboot"
                    };
                }

                Console.WriteLine(new string('=', warning.Length));
                Console.WriteLine(warning);
                Console.WriteLine(new string('=', warning.Length));
                Console.WriteLine();

                client.Connect();
                using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

                var index = 0;
                foreach (var command in commands)
                {
                    if (!reset)
                    {
                        switch (index)
                        {
                            case 1:
                                Console.WriteLine("Setting login credentials, time zone...");
                                break;
                            case 14:
                                Console.WriteLine("Setting SIP server settings...");
                                break;
                            case 19:
                                Console.WriteLine("Setting local dialer settings...");
                                break;
                            case 32:
                                Console.WriteLine("Saving changes and rebooting...");
                                break;
                        }
                    }

                    sshStream.WriteLine(command);
                    // uncomment this to see what's being sent/received
                    // string line;
                    // while((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(200))) != null)
                    //     Console.WriteLine(line);
                    Thread.Sleep(100);
                    index++;
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

                Console.Write(reset
                    ? "ATA successfully reset, press any key to continue..."
                    : "ATA successfully configured, press any key to exit...");
                Console.ReadKey();
                if (reset)
                {
                    reset = false;
                    continue;
                }

                Console.WriteLine("Have a nice day ＜（＾－＾）＞");
                Console.WriteLine();
                for (var i = 0; i < 3; i++)
                {
                    Console.Write(".");
                    Thread.Sleep(1000);
                }

                break;
            }
        }
        
        
        // interface scanning

        /* UP FRONT - I'd like to disclaim that this is probably not the best way to do this!
         * This essentially just skips through all interfaces with the ASSUMPTION that only one will meet all requirements
         * of being Ethernet or 802.11, being in UP status, and not being described as virtual
         * I am completely open to better ways to do this
         */
        private static NetworkInterface GetInterface()
        {
            var client = new TcpClient();

            var bytes = Array.Empty<byte>();
            
            // for every interface on the computer
            foreach (var iInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                // if it's not ethernet or wireless, we're not interested
                if (iInterface.NetworkInterfaceType != NetworkInterfaceType.Ethernet &&
                    iInterface.NetworkInterfaceType != NetworkInterfaceType.Wireless80211) continue;
                // if it's not UP, we're not interested
                if (iInterface.OperationalStatus != OperationalStatus.Up) continue;
                // if it's described as a virtual adapter, we're not interested
                if (iInterface.Description.ToLower().Contains("virtual")) continue;
                
                // get the list of Unicast Addresses
                foreach (var ip in iInterface.GetIPProperties().UnicastAddresses)
                {
                    // if it isn't a private IP, we're not interested
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    // otherwise, save the IP
                    bytes = IPAddress.Parse(ip.Address.ToString()).GetAddressBytes();
                    break;
                }

                // did we get an IP?
                if (bytes == Array.Empty<byte>())
                    continue;

                // for each gateway IP we've defined
                foreach (var gateway in Gateways)
                {
                    bytes[3] = (byte) gateway;
                    IPAddress newIp = new(bytes);
                    try
                    {
                        // if we can't connect, move onto the next gateway
                        if (!client.ConnectAsync(newIp, 80).Wait(20)) continue;
                    }
                    catch (Exception)
                    {
                        // ignored
                    }

                    // if we got here, we found it!
                    Console.WriteLine("Found interface: " + iInterface.Name);
                    return iInterface;
                }
            }
            Console.Write("Hmm... looks like we can't find a proper gateway on this network...");
            Environment.Exit(-1);
            throw new InvalidOperationException();
        }

        
        // full port scanning for locating ATA
        
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
                        if (!scan.ConnectAsync(newIp, s).Wait(20)) continue;
                        var macAddress = GetMacByIp(newIp.ToString());
                        Console.WriteLine($"{newIp}[{s}] | FOUND, MAC: {macAddress}", Color.Green);
                        if (!IsGrandstream(macAddress)) continue;
                        _ip = newIp.ToString();
                        return true;
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
            // should you wish to add more ATAs, add their MAC regex here and change the return type to int
            // then create a switch statement to act accordingly
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

    }
}