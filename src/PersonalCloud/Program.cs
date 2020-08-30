using Amazon.CDK;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersonalCloud
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var environment = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? "Staging";
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false, true)
                .AddJsonFile("appsettings." + environment + ".json", true, true)
                .AddEnvironmentVariables()
                .Build();

            var app = new App();
            var stack = new PersonalCloudStack(configuration, app, "PersonalCloudStack-" + environment);
            app.Synth();
        }
    }
}
