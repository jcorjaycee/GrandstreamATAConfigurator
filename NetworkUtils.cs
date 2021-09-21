using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace GrandstreamATAConfigurator
{
    public static class NetworkUtils
    {
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
            2,
            3,
            50,
            254
        };

        // interface scanning

        /* UP FRONT - I'd like to disclaim that this is probably not the best way to do this!
         * This essentially just skips through all interfaces with the ASSUMPTION that one will meet all requirements
         * of being Ethernet or 802.11, being in UP status, and not being described as virtual
         * It can still fully miss interfaces which may not be valid for this purpose
         * I am completely open to better ways to do this
         */

        public static NetworkInterface GetInterface()
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
                    bytes[3] = (byte)gateway;
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
                    if (!Program.GetUserBool("Is this the correct interface?")) break;
                    return iInterface;
                }
            }

            Console.WriteLine();
            Console.WriteLine("Hmm... looks like we can't find any interfaces that will work for this...");
            Console.ReadKey();
            Environment.Exit(-1);
            throw new InvalidOperationException();
        }

        private static bool IsGrandstream(string mac)
        {
            // should you wish to add more ATAs, add their MAC regex here and change the return type to int
            // then create a switch statement to act accordingly
            const string pattern = "^([Cc][0][-:][7][4][-:][Aa][Dd][:-])([0-9A-Fa-f]{2}[:-]){2}([0-9A-Fa-f]{2})$";
            return Regex.IsMatch(mac, pattern);
        }

        public static bool PortScan(string ip, out string newIp)
        {
            // for each possible IPv4
            for (var i = 1; i < 255; i++)
            {
                var bytes = IPAddress.Parse(ip).GetAddressBytes();
                bytes[3] = (byte)i;
                IPAddress newIpBuilder = new(bytes);

                foreach (var s in Ports)
                {
                    using var scan = new TcpClient();
                    try
                    {
                        // if we can't connect to the IP in question, move on
                        try
                        {
                            if (!scan.ConnectAsync(newIpBuilder, s).Wait(20)) continue;
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        // found a device that responds to one of the ports!
                        var macAddress = GetMacByIp(newIpBuilder.ToString());
                        // uncomment the below line to output each IP found to the console
                        // Console.WriteLine($"{newIpBuilder}[{s}] | FOUND, MAC: {macAddress}", Color.Green);
                        // if it's not a Grandstream device we're not going any further
                        if (!IsGrandstream(macAddress)) continue;
                        newIp = newIpBuilder.ToString();
                        return true;
                    }
                    catch (Exception e) // probably a network error
                    {
                        Console.WriteLine("Whoops, couldn't get that... " + newIpBuilder);
                        Console.WriteLine(e);
                        throw;
                    }
                }
            }

            newIp = "";
            return false;
        }

        // PARTIAL CREDIT FOR THIS GOES TO compman2408 ON StackOverflow
        // https://stackoverflow.com/a/24814027
        public static string GetLocalIPv4(NetworkInterface @interface)
        {
            var output = "";
            foreach (var ip in @interface.GetIPProperties().UnicastAddresses)
            {
                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    output = ip.Address.ToString();
                }
            }

            return output;
        }

        // CREDIT FOR ALL BELOW CODE GOES TO Mauro Sampietro ON StackOverflow
        // https://stackoverflow.com/a/51501665
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
            var pProcess = new Process
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
    }
}