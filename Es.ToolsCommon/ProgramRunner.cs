using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Es.ToolsCommon
{
    public static class ProgramRunner
    {
        internal sealed class AsyncStreamReadState
        {
            public TextWriter TextWriter;
            public Stream Stream;
            public byte[] Buffer;
            public IAsyncResult AsyncResult;
            public bool Complete;
        }
        internal sealed class ProgramInfo
        {
            public Process Process;
            public AsyncStreamReadState[] StreamArray;
        }
        private const int StreamReadBufferSize = 1024;
        
        public static void Run(
            string programName,
            string arguments=null,
            string workingDirectory=null,
            TextWriter outputTextWriter=null,
            int timeoutMilliseconds = int.MaxValue)
        {
            var processStartInfo =
                new ProcessStartInfo
                    {
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        ErrorDialog = false,
                        UseShellExecute = false,
                        FileName = programName,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        CreateNoWindow = true,
                    };

            if (arguments != null)
                processStartInfo.Arguments = arguments;

            if (workingDirectory != null)
                processStartInfo.WorkingDirectory = workingDirectory;

            if (!File.Exists(programName))
                throw new ArgumentException(
                    "Could not find startInfo.ProgramName \""
                    + programName
                    + "\" to run."
                    );

// ReSharper disable UseObjectOrCollectionInitializer
            // Object Collection Initializer can cause the creation of a temporary that doesn't get Dispose() called on it.
            var process = new Process();
// ReSharper restore UseObjectOrCollectionInitializer

            process.StartInfo = processStartInfo;

            if (!process.Start())
                throw new Exception("Could not start process: " + programName);

            process.StandardInput.Close();

            var streamArray =
                new[]
                {
                    new AsyncStreamReadState
                    {
                        Stream = process.StandardOutput.BaseStream,
                        Buffer = new byte[StreamReadBufferSize],
                        AsyncResult = null,
                        TextWriter = outputTextWriter
                    },
                    new AsyncStreamReadState
                    {
                        Stream = process.StandardError.BaseStream,
                        Buffer = new byte[StreamReadBufferSize],
                        AsyncResult = null,
                        TextWriter = outputTextWriter
                    }
                };
            var programInfo = new ProgramInfo { Process=process, StreamArray=streamArray };

            foreach (var s in streamArray)
                s.AsyncResult = s.Stream.BeginRead(s.Buffer, 0, s.Buffer.Length, ContinueRead, s);
                
            if (!programInfo.Process.WaitForExit(timeoutMilliseconds))
                return;

            for (;;)
            {
                var waitHandles =
                    programInfo.StreamArray.Where(x => !x.Complete).Select(x => x.AsyncResult.AsyncWaitHandle).
                        ToArray();
                if (waitHandles.Length == 0) break;
                WaitHandle.WaitAll(waitHandles);
            }
            programInfo.Process.Dispose();
        }

        private static void ContinueRead(IAsyncResult ar)
        {
            var s = (AsyncStreamReadState) ar.AsyncState;
            var nr = s.Stream.EndRead(ar);
            if (nr <= 0)
            {
                s.Complete = true;
                return;
            }
            s.TextWriter?.Write(Encoding.ASCII.GetString(s.Buffer, 0, nr));
            s.AsyncResult = s.Stream.BeginRead(s.Buffer, 0, s.Buffer.Length, ContinueRead, s);
        }
        
    }
}