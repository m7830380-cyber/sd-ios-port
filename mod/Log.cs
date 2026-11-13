using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SdIOS
{
    public static class Log
    {
        static StreamWriter s_Writer;
        static readonly object s_Lock = new object();
        public static readonly List<string> Buffer = new List<string>(512);
        public const int MaxBuffer = 300;

        public static void Init(string docsDir)
        {
            try
            {
                string cur = Path.Combine(docsDir, "SD-iOS.log");
                string prev = Path.Combine(docsDir, "SD-iOS-prev.log");
                if (File.Exists(prev)) File.Delete(prev);
                if (File.Exists(cur)) File.Move(cur, prev);

                var fs = new FileStream(cur, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                s_Writer = new StreamWriter(fs) { AutoFlush = true };
                Application.logMessageReceived += OnUnityLog;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[SD-iOS] Log.Init failed: " + e.Message);
            }
        }

        static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                Write($"[{type}] {condition}\n{stackTrace}");
            else
                Write($"[{type}] {condition}");
        }

        public static void Info(string msg) => Write("[INFO] " + msg);
        public static void Warn(string msg) => Write("[WARN] " + msg);
        public static void Error(string msg) => Write("[ERR]  " + msg);

        static void Write(string line)
        {
            string stamped = $"[{DateTime.UtcNow:HH:mm:ss.fff}] {line}";
            lock (s_Lock)
            {
                s_Writer?.WriteLine(stamped);
                if (Buffer.Count >= MaxBuffer) Buffer.RemoveAt(0);
                Buffer.Add(stamped);
            }
        }
    }
}
