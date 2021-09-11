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
        internal static IWebHost Builder;
        
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
        // timer is set to 45s by default
        // max amount of time for an update part to push in my testing was 40s
        // if updates significantly grow in size, this may need to be increased
        private static readonly Timer ServerTimer = new(45000);
        
        public void Configure(IApplicationBuilder app)
        {
            ServerTimer.Elapsed += ServerTimerOnElapsed;
            ServerTimer.Start();

            var stage = 0;
            
            app.Use(async (_, next) =>
            {
                Console.WriteLine("Update stage " + stage++ + "...");
                ServerTimer.Stop();
                ServerTimer.Start();

                // Call the next delegate/middleware in the pipeline
                await next();
            });

            app.UseStaticFiles();
        }
        
        
        private static void ServerTimerOnElapsed(object sender, ElapsedEventArgs e)
        {
            Server.Builder.StopAsync();
            ServerTimer.Dispose();
        }
    }
}