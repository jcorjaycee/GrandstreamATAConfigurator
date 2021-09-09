using System;
using System.IO;
using System.Threading.Tasks;
using System.Timers;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace GrandstreamATAConfigurator
{
    public static class Server
    {
        public static IWebHost Builder;
        
        public static void StartServer(string ip)
        {
            Builder = BuildWebHost(ip);

            Task.Run(() =>
            {
                Builder.RunAsync();
            });
            
            Builder.WaitForShutdown();
        }
        
        private static IWebHost BuildWebHost(string ip)
        {
            var path = Path.Join(Directory.GetCurrentDirectory(), "assets");

            Environment.CurrentDirectory = path;
            return WebHost.CreateDefaultBuilder()
                .UseStartup<Startup>()
                .ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
                .UseKestrel()
                .UseContentRoot(path)
                .UseWebRoot(path)
                .UseUrls($"http://{ip}")
                .SuppressStatusMessages(true)
                .Build();
        }
    }

    public class Startup
    {
        private static readonly Timer ServerTimer = new(120000);
        
        public void Configure(IApplicationBuilder app)
        {
            
            ServerTimer.Elapsed += ServerTimerOnElapsed;
            ServerTimer.Start();
            
            app.UseStaticFiles();
            
            app.Run( async _ =>
            {
                ServerTimer.Interval = ServerTimer.Interval; // restart timer
                Console.WriteLine("Request!");
            });
        }
        
        
        private static void ServerTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            Console.WriteLine("Shutting down server...");
            Server.Builder.StopAsync();
            ServerTimer.Dispose();
        }
    }
}