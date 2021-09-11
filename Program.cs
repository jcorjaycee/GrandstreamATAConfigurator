using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace GrandstreamATAConfigurator
{
    internal static class Program
    {
        // BEGIN GLOBAL VARIABLES

        // for locating, connecting to ATA
        private static NetworkInterface _interfaceToUse;
        private static string _ataIp = "";
        private const int Port = 80;

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

        // END GLOBAL VARIABLES


        // MAIN
        private static void Main()
        {
            // print title card
            Console.Clear();
            const string title = "Grandstream SSH Setup";
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine(title);
            Console.WriteLine(new string('=', title.Length));
            Console.WriteLine();

            // find which interface we should be on - WiFi, Ethernet, etc
            _interfaceToUse = NetworkUtils.GetInterface();

            // location of web server, should we need to upgrade firmware
            _serverIp = NetworkUtils.GetLocalIPv4(_interfaceToUse);

            // this variable gets mutated later to represent the ATA IP
            // we declare it here to get the proper subnet
            _ataIp = NetworkUtils.GetLocalIPv4(_interfaceToUse);

            Console.WriteLine();
            Console.WriteLine("Now scanning your network for a Grandstream device... Please wait.");
            if (NetworkUtils.PortScan(_ataIp, out _ataIp)) // we found something!
                Console.WriteLine("Grandstream device found! Using IP: " + _ataIp);
            else // no devices found...
            {
                Console.Write("Oops, we can't find a Grandstream device on this network. Make " +
                              "sure you're connected to the right network, then try again.");
                Console.ReadKey();
                return;
            }

            AttemptConnect(); // try connecting to the ATA, prompt for credentials if needed
            Console.Clear();

            // ssh into ATA and check if ATA is up to date
            var client = new SshClient(_ataIp, Username, _password);
            if (!IsUpToDate(false))
            {
                client.Connect();
                using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

                var commands = new[]
                {
                    "config",
                    // set server type: HTTP
                    "set 212 1",
                    // set server address
                    $"set 192 {_serverIp}:{Port}",
                    // save and exit
                    "commit",
                    "exit",
                    // begin upgrade
                    "upgrade",
                    "upgrade",
                    "y"
                };

                Console.Clear();
                const string updatingMessage =
                    "The ATA will now upgrade its firmware. Do NOT touch anything during this process.";
                Console.WriteLine(new string('=', updatingMessage.Length));
                Console.WriteLine(updatingMessage);
                Console.WriteLine(new string('=', updatingMessage.Length));
                foreach (var command in commands)
                {
                    sshStream.WriteLine(command); // write the command to the stream
                    // uncomment this to see what's being sent/received
                    // string line;
                    // while((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(200))) != null)
                    //     Console.WriteLine(line);
                    Thread.Sleep(100);
                }

                // give the ATA a moment to catch up
                Thread.Sleep(200);

                // disconnect while updating
                sshStream.Close();
                client.Disconnect();

                // start HTTP server for firmware hosting
                Server.StartServer(_serverIp, Port, _ataIp);

                // server has now been closed, however we need to give the ATA a moment
                // before it begins its reboot
                Thread.Sleep(10000);

                // ping ATA until we receive a response
                for (var i = 0; i < 60; i++)
                {
                    if (i == 59)
                    {
                        Console.WriteLine("Could not reconnect to the ATA. " +
                                          "Please ensure it is online, then re-run the program.");
                        Console.ReadKey();
                        Environment.Exit(-19);
                    }

                    try
                    {
                        new TcpClient().ConnectAsync(_ataIp, 80);
                        break;
                    }
                    catch (SocketException)
                    {
                        Thread.Sleep(5000);
                    }
                }

                if (!IsUpToDate(true))
                {
                    const string updateFailedError = "THE UPDATE FAILED. Please investigate manually...";
                    Console.WriteLine(new string('=', updateFailedError.Length));
                    Console.WriteLine(updateFailedError);
                    Console.WriteLine(new string('=', updateFailedError.Length));
                    Console.ReadKey();
                    Environment.Exit(-11);
                }
            }

            // gather user data for configuration
            GetParams();

            // take action to reset or configure ATA
            ResetOrConfigureAta(_reset);
        }

        // extensions of main (for readability)

        private static void AttemptConnect()
        {
            var client = new SshClient(_ataIp, Username, _password); // init SshClient

            Console.WriteLine("Attempting connection...");

            // just try connecting using default credentials, if they work awesome, if not prompt
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
                        // redeclare client with new password
                        client = new SshClient(_ataIp, Username, _password);
                        client.Connect();
                        client.Disconnect();
                        return;
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // if we got here, the password attempts failed three times

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
                Environment.Exit(2);
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

                // due to phone number verification being a little more complex, it splits off here
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
                if (_phoneNumber.Length == 10 && !_phoneNumber.StartsWith("1")) // if the number doesn't start with a 1
                    _phoneNumber = "1" + _phoneNumber;
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
                var client = new SshClient(_ataIp, Username, _password); // init SshClient
                string warning;
                string[] commands;

                Console.Clear();

                if (reset)
                {
                    warning = "We will now reset the ATA. Do NOT touch anything during this process.";
                    commands = new[] { "reset 0", "y" };
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

                // try to connect; if authentication fails, a reset may have set it back to defaults
                try
                {
                    client.Connect();
                }
                catch (SshAuthenticationException)
                {
                    try
                    {
                        client = new SshClient(_ataIp, "admin", "admin");
                        client.Connect();
                    }
                    catch (SshAuthenticationException)
                    {
                        Console.WriteLine("Something's gone wrong, and the login credentials have changed.");
                        Console.WriteLine("Please restart or hardware factory reset your ATA, then try again.");
                        Console.ReadKey();
                        Environment.Exit(-3);
                    }
                    catch
                    {
                        Console.WriteLine("We weren't able to reconnect to the ATA.");
                        Console.WriteLine("Please restart or hardware factory reset your ATA, then try again.");
                        Console.ReadKey();
                        Environment.Exit(-4);
                    }
                }

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
                            case 34:
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
                client = new SshClient(_ataIp, Username, _adminPassword);

                Thread.Sleep(30000);

                // continually ping ATA until it comes online
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

                if (!client.IsConnected)
                {
                    Console.WriteLine("WARNING: Couldn't connect to the ATA after reboot.");
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

                Console.WriteLine();
                Console.WriteLine("Have a nice day!");
                for (var i = 0; i < 3; i++)
                {
                    Console.Write(".");
                    Thread.Sleep(1000);
                }

                break;
            }
        }

        public static bool GetUserBool(string prompt)
        {
            while (true)
            {
                Console.Write(prompt + " (Y/n): ");
                var reset = Console.ReadKey().Key;
                switch (reset)
                {
                    case ConsoleKey.Enter:
                    case ConsoleKey.Y:
                        return true;
                    case ConsoleKey.N:
                        return false;
                    default:
                        Console.WriteLine("Sorry, that wasn't a valid input.");
                        break;
                }
            }
        }

        private static bool IsUpToDate(bool skipPrompt)
        {
            if (!skipPrompt)
            {
                if (!File.Exists(Path.Join(Directory.GetCurrentDirectory(), "assets/ht801fw.bin")))
                {
                    Console.WriteLine("Not seeing any update files, so not checking for an update...");
                    for (var i = 0; i < 3; i++)
                    {
                        Console.Write(".");
                        Thread.Sleep(1000);
                    }

                    return true;
                }

                try
                {
                    // try to get the version from the version file
                    var sr = new StreamReader("assets/version");
                    _currentVersionNumber = new Version(sr.ReadLine() ?? throw new InvalidOperationException());
                }
                catch (Exception)
                {
                    Console.WriteLine("Seems the server is missing a valid version file." +
                                      " Please add one if you wish to enable updates!");
                    for (var i = 0; i < 3; i++)
                    {
                        Console.Write(".");
                        Thread.Sleep(1000);
                    }

                    return true;
                }
            }

            using var client = new SshClient(_ataIp, Username, _password);

            for (var i = 0; i < 60; i++)
            {
                if (i == 59)
                {
                    Console.WriteLine("Could not reconnect to the ATA. " +
                                      "Please ensure it is online, then re-run the program.");
                    Console.ReadKey();
                    Environment.Exit(-19);
                }

                try
                {
                    client.Connect();
                    break;
                }
                catch (SocketException)
                {
                    Thread.Sleep(5000);
                }
            }

            using var sshStream = client.CreateShellStream("ssh", 80, 40, 80, 40, 1024);

            // request status to get ATA version
            sshStream.WriteLine("status");
            // go through each line
            string line;
            while ((line = sshStream.ReadLine(TimeSpan.FromMilliseconds(2000))) != null)
            {
                if (line.ToLower().Contains("program --"))
                {
                    var foundVersionNumber = new Version(line[15..]); // program string starts 15 characters in
                    if (skipPrompt)
                        return _currentVersionNumber == foundVersionNumber;
                    Console.WriteLine("Found program version: " + foundVersionNumber);
                    Console.WriteLine("Most up-to-date program version: " + _currentVersionNumber);
                    if (_currentVersionNumber > foundVersionNumber)
                    {
                        // we ! this because UpToDate() returns false if we need to upgrade
                        return !GetUserBool("ATA is out of date! Shall we upgrade?");
                    }

                    if (_currentVersionNumber <= foundVersionNumber)
                    {
                        return true;
                    }
                }

                Thread.Sleep((100));
            }

            Console.WriteLine("Couldn't get a version number. " +
                              "This usually happens when the program was started too soon after the ATA booted up.");
            Console.WriteLine("Try running the program again. If the problem persists, contact the developer.");
            Console.ReadKey();
            Environment.Exit(-2);
            throw new InvalidOperationException();
        }
    }
}