﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NLog;
using NLog.Config;
using NLog.Targets;
using Raven.Client.Document;
using Raven.Client.Embedded;
using ServiceControl.MessageFailures;

namespace FailedMessageCleaner
{
    class Program
    {
        static async Task Main(string[] args)
        {
            ConfigureLogging();

            if (args.Length == 0)
            {
                Console.WriteLine("Usage: FileMessageCleaner.exe <db_path>");
                return;
            }

            var path = args[0]; // @"C:\ProgramData\Particular\ServiceControl\Particular.Rabbitmq\DB";

            log.Info($"Connecting to RavenDB instance at {path} ...");

            var store = RavenBootstrapper.Setup(path, 33334);
            store.Conventions.DefaultQueryingConsistency = ConsistencyOptions.AlwaysWaitForNonStaleResultsAsOfLastWrite;
            store.Initialize();

            log.Info($"Connected. Processing FailedMessage documents ...");

            await CleanFailedMessages(store).ConfigureAwait(false);
            log.Info($"Clean-up finished press <any key> to exit ...");
            Console.ReadLine();

            store.Dispose();
        }

        static async Task CleanFailedMessages(EmbeddableDocumentStore store)
        {
            int start = 0;
            //This makes sure we don't do more than 30 operations per session
            int pageSize = 15;
            int maxAttemptsPerMessage = 10;

            var ids = new List<string>();

            while (true)
            {
                ids.Clear();

                using (var session = store.OpenSession())
                {
                    var messages = session
                        .Query<FailedMessage>()
                        .Where(x => x.ProcessingAttempts.Count > maxAttemptsPerMessage)
                        .Take(pageSize)
                        .ToList();

                    start += messages.Count;

                    if (messages.Count == 0)
                    {
                        log.Info("Scanned {0:N0} documents.", start);
                        return;
                    }

                    foreach (var message in messages)
                    {
                        log.Info("Processing: {0} truncating {1:N0} processed attempts ", message.UniqueMessageId, message.ProcessingAttempts.Count);

                        message.ProcessingAttempts = message.ProcessingAttempts
                            .OrderByDescending(pa => pa.AttemptedAt)
                            .Take(maxAttemptsPerMessage)
                            .ToList();
                    }

                    session.SaveChanges();
                }
            }
        }
        static void ConfigureLogging()
        {
            var config = new LoggingConfiguration();
            var consoleTarget = new ConsoleTarget
            {
                Name = "console",
                Layout = "${processtime}|${level:uppercase=true}|${message}",
            };

            config.AddRule(LogLevel.Info, LogLevel.Fatal, consoleTarget, "*");

            LogManager.Configuration = config;
        }

        static Logger log = LogManager.GetCurrentClassLogger();
    }
}
