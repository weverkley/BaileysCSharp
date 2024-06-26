﻿using Proto;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BaileysCSharp.Core.Helper
{
    public enum LogLevel
    {
        Fatal = 1,
        Error = 2,
        Warn = 4,
        Info = 8,
        Debug = 16,
        Trace = 32,
        All = 63,
        Raw = 64,
    }

    public class Logger
    {
        private static object locker = new object();
        public LogLevel Level { get; set; }


        internal void Error(string message)
        {
            if (Level >= LogLevel.Error)
            {
                var logEntry = new
                {
                    level = LogLevel.Error,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    message
                };
                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }
        public void Error(object? obj, string message)
        {
            if (Level >= LogLevel.Error)
            {
                var logEntry = new
                {
                    level = LogLevel.Error,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        public void Error(Exception ex, string message)
        {
            if (Level >= LogLevel.Error)
            {
                var logEntry = new
                {
                    level = LogLevel.Error,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    error = ex.Message,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        internal void Warn(object? obj, string message)
        {
            if (Level >= LogLevel.Warn)
            {
                var logEntry = new
                {
                    level = LogLevel.Warn,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };
                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        internal void Warn(string message)
        {
            var logEntry = new
            {
                level = LogLevel.Warn,
                time = DateTime.Now,
                hostname = Dns.GetHostName(),
                message
            };

            lock (locker)
            {
                var backup = Console.ForegroundColor;
                Console.ForegroundColor = ConsoleColor.Yellow;
                Write(logEntry);
                Console.ForegroundColor = backup;
            }
        }

        internal void Info(string message)
        {
            if (Level >= LogLevel.Info)
            {
                var logEntry = new
                {
                    level = LogLevel.Info,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        internal void Info(object? obj, string message)
        {
            if (Level >= LogLevel.Info)
            {
                var logEntry = new
                {
                    level = LogLevel.Info,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }
        internal void Debug(string message)
        {
            if (Level >= LogLevel.Debug)
            {
                var logEntry = new
                {
                    level = LogLevel.Debug,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        internal void Debug(object? obj, string message)
        {
            if (Level >= LogLevel.Debug)
            {
                var logEntry = new
                {
                    level = LogLevel.Debug,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        internal void Trace(string message)
        {
            if (Level >= LogLevel.Trace)
            {
                var logEntry = new
                {
                    level = LogLevel.Trace,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.White;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }

        public void Trace(object? obj, string message)
        {
            if (Level >= LogLevel.Trace)
            {
                var logEntry = new
                {
                    level = LogLevel.Trace,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.White;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }



        private void Write(object logEntry)
        {
            var settings = new JsonSerializerOptions()
            {
                Converters = { new Base64Converter(), new ByteStringConverter() },
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            //settings.Converters = new List<JsonConverter>() { new Base64Converter(), new ByteStringConverter(), new Newtonsoft.Json.Converters.StringEnumConverter() };
            //settings.ContractResolver = new ConditionalResolver();
            //settings.NullValueHandling = NullValueHandling.Ignore;
            var json = JsonSerializer.Serialize(logEntry, settings);
            System.Diagnostics.Debug.WriteLine(json);
            Console.WriteLine(json);
        }

        internal void Raw(object obj, string message)
        {
            if (Level >= LogLevel.Raw)
            {
                var logEntry = new
                {
                    level = LogLevel.Raw,
                    time = DateTime.Now,
                    hostname = Dns.GetHostName(),
                    traceobj = obj,
                    message
                };

                lock (locker)
                {
                    var backup = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Blue;
                    Write(logEntry);
                    Console.ForegroundColor = backup;
                }
            }
        }
    }
    //public class ConditionalResolver : DefaultContractResolver
    //{

    //    protected override JsonProperty CreateProperty(MemberInfo member, MemberSerialization memberSerialization)
    //    {
    //        JsonProperty prop = base.CreateProperty(member, memberSerialization);

    //        if (prop.PropertyName.StartsWith("Has") && prop.PropertyType == typeof(bool))
    //        {
    //            return null;
    //        }

    //        return prop;
    //    }
    //}
}