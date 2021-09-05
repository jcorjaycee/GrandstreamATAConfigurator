using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GrandstreamATAConfigurator
{
    internal static class Program
    {
        // BEGIN GLOBAL VARIABLES
        
        // for locating, connecting to ATA
        private static NetworkInterface _interfaceToUse;
        private static string _ip = "";
        
        // for firmware upgrades
        private static Version _currentVersionNumber;
        private static string _serverIp;

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

        // used to port scan for ATA
        private static readonly int[] Ports = new[]
        {
            80,
            443
        };

        // used to validate if we're using the right interface
        private static readonly int[] Gateways = new[]
        {
            0,
            1,
            50,
            254
        };

        // END GLOBAL VARIABLES
        
        
        // MAIN
        private static void Main()
        {
            // print title card
            const string title = "Start.ca Grandstream SSH Setup";
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine(title);
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine();

            // find which interface we should be on - WiFi, Ethernet, etc
            _interfaceToUse = GetInterface();
            
            // location of web server, should we need to upgrade firmware
            _serverIp = GetLocalIPv4(_interfaceToUse.NetworkInterfaceType) + ":80";

            // this variable gets mutated later to represent the ATA IP
            // we declare it here to get the proper subnet
            _ip = GetLocalIPv4(_interfaceToUse.NetworkInterfaceType);

            Console.WriteLine();
            Console.WriteLine("Now scanning your network for a Grandstream device...");
            if (PortScan())
                Console.WriteLine("Grandstream device found! Using IP: " + _ip);
            else
            {
                Console.Write("Oops, we can't find a Grandstream device on this network. Make " +
                              "sure you're connected to the right network, then try again.");
                Console.ReadKey();
                return;
            }
            
            AttemptConnect();

            Console.Clear();

            var client = new SshClient(_ip, Username, _password);

            if (!UpToDate())
            {
                client.Connect();
                using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

                var commands = new[]
                {
                    "config",
                    // set server type: HTTP
                    "set 212 1",
                    // set server address
                    $"set 192 {_serverIp}",
                    // save and exit
                    "commit",
                    "exit",
                    // begin upgrade
                    "upgrade",
                    "upgrade",
                    "y"
                };

                Console.WriteLine("ATA is out of date! Running upgrade...");
                foreach (var command in commands)
                {
                    sshStream.WriteLine(command);
                    // uncomment this to see what's being sent/received
                    // string line;
                    // while((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(200))) != null)
                    //     Console.WriteLine(line);
                    Thread.Sleep(100);
                }
                Thread.Sleep(200);
                sshStream.Close();
                client.Disconnect();
                Console.WriteLine("Waiting 3 minutes...");
                var serverThread = new Thread(Server);
                serverThread.Start();
                // TODO: Figure out how to stop this server after x amount of time...
                Thread.Sleep(180000);
                
            }

            GetParams();

            ResetOrConfigureAta(_reset);
        }

        // extensions of main (for readability)
        
         private static void AttemptConnect()
        {
            var client = new SshClient(_ip, Username, _password);

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
                        client = new SshClient(_ip, Username, _password);
                        client.Connect();
                        client.Disconnect();
                        return;
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
                Environment.Exit(0);
            }

            Console.WriteLine("OK!");
            for (var i = 0; i < 3; i++)
            {
                Console.Write(".");
                Thread.Sleep(1000);
            }
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
                _authenticatePassword = Console.ReadLine();
                while (true)
                {
                    Console.WriteLine();
                    Console.Write("Your primary server? ");
                    _primaryServer = Console.ReadLine();
                    if (_primaryServer == string.Empty)
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
                    if (_adminPassword == string.Empty)
                        Console.WriteLine("ATA password cannot be empty...");
                    else
                        break;
                }

                Console.Clear();

                _reset = GetUserBool("Are we resetting the ATA first?");

                Console.Clear();
                Console.WriteLine("Current password: " + _password);
                Console.WriteLine("New password: " + _adminPassword);
                Console.WriteLine("VoIP phone number to add: " + _phoneNumber);
                Console.WriteLine("SIP password: " + _authenticatePassword);
                Console.WriteLine("Primary server: " + _primaryServer);
                Console.WriteLine("Failover server: " + _failoverServer);
                Console.WriteLine("Resetting the ATA first: " + _reset);
                Console.WriteLine();

                done = GetUserBool("Is all of the above correct?");
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
                var client = new SshClient(_ip, Username, _password);
                string warning;
                string[] commands;

                Console.Clear();


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
                        // vocoder 8 and 9
                        "set 98 0",
                        "set 46 0",
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
                
                // password may have changed, redeclare client
                client = new SshClient(_ip, Username, _adminPassword);

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
                
                client.Dispose();

                Console.WriteLine("Have a nice day!");
                Console.WriteLine();
                for (var i = 0; i < 3; i++)
                {
                    Console.Write(".");
                    Thread.Sleep(1000);
                }

                break;
            }
        }

        private static bool GetUserBool(string prompt)
        {
            while (true)
            {
                Console.Write(prompt + " (Y/n): ");
                var reset = Console.ReadKey().Key;
                // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
                switch (reset)
                {
                    case ConsoleKey.Enter:
                    case ConsoleKey.Y:
                        return true;
                    case ConsoleKey.N:
                        return false;
                }

                Console.WriteLine("Sorry, that wasn't a valid input.");
            }
        }

        private static bool UpToDate()
        {
            using var client = new SshClient(_ip, Username, _password);
            client.Connect();
            using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

            try
            {
                // try to get the version from the version file
                var sr = new StreamReader("version");
                _currentVersionNumber = new Version(sr.ReadLine() ?? throw new InvalidOperationException());
            }
            catch (Exception)
            {
                Console.WriteLine("Seems the server is missing a version file. That's a problem!");
                Console.ReadKey();
                Environment.Exit(-3);
            }

            // request status to get ATA version
            sshStream.WriteLine("status");
            // go through each line
            string line;
            while ((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(200))) != null)
            {
                if (line.ToLower().Contains("program --"))
                {
                    var foundVersionNumber = new Version(line[15..]); // program string starts 15 characters in
                    Console.WriteLine("Found program version: " + foundVersionNumber);
                    Console.WriteLine("Most up-to-date program version: " + _currentVersionNumber);
                    if (_currentVersionNumber > foundVersionNumber)
                    {
                        return !GetUserBool("ATA is out of date! Shall we upgrade?");
                    }
                }

                Thread.Sleep((100));
            }

            Console.WriteLine("Couldn't get a version number. This is a big problem!");
            Console.ReadKey();
            Environment.Exit(-2);
            throw new InvalidOperationException();
        }


        // interface scanning

        /* UP FRONT - I'd like to disclaim that this is probably not the best way to do this!
         * This essentially just skips through all interfaces with the ASSUMPTION that one will meet all requirements
         * of being Ethernet or 802.11, being in UP status, and not being described as virtual
         * It can still fully miss interfaces which may not be valid for this purpose
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
                if (iInterface.Description.ToLower().Contains("multiplexor")) continue;

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
                    Console.WriteLine();
                    Console.WriteLine("Found interface: " + iInterface.Name);
                    if (!GetUserBool("Is this the correct interface?")) break;
                    return iInterface;
                }
            }

            Console.WriteLine();
            Console.Write("Hmm... looks like we can't find any interfaces that will work for this...");
            Console.ReadKey();
            Environment.Exit(-1);
            throw new InvalidOperationException();
        }


        // full port scanning for locating ATA

        private static bool PortScan()
        {
            // for each possible IPv4
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
                        // if we can't connect to the IP in question, move on
                        if (!scan.ConnectAsync(newIp, s).Wait(20)) continue;
                        // found a device that responds to one of the ports!
                        var macAddress = GetMacByIp(newIp.ToString());
                        Console.WriteLine($"{newIp}[{s}] | FOUND, MAC: {macAddress}", Color.Green);
                        // if it's not a Grandstream device we're not going any further
                        if (!IsGrandstream(macAddress)) continue;
                        _ip = newIp.ToString();
                        return true;
                    }
                    catch (Exception e) // probably a network error
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
        private static void Server()
        {
            Console.WriteLine("Starting server...");
            // TODO: Figure out how to shut this server down when done so it doesn't keep spewing info onto the console
            // and so the app exits properly
            WebHost.CreateDefaultBuilder()
                .Configure(config => config.UseStaticFiles())
                .UseUrls("http://" + _serverIp)
                .UseWebRoot("").Build().Run();
        }
    }
}
