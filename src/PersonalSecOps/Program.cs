using Amazon.CDK;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace PersonalSecOps
{
    sealed class Program
    {
        public static void Main(string[] args)
        {
            var configuration = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false, true)
                .AddEnvironmentVariables()
                .Build();

            var app = new App();
            var stack = new PersonalSecOpsStack(configuration, app, "PersonalSecOpsStack");
            app.Synth();
        }
    }
}
