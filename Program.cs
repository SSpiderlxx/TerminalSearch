namespace TerminalSearch;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Spectre.Console;
using Spectre.Console.Rendering;

class NativeDirSearch
{
    // Constants for openat and directory handling
    const int AT_FDCWD = -100; // Use current working directory
    const int O_RDONLY = 0; // Open for reading only
    const int O_DIRECTORY = 0x0200000; // Must be a directory
    const int O_CLOEXEC = 0x80000; // Set close-on-execute flag
    const int NAME_MAX = 255; // Maximum filename length
    // getdents64 is not used; we rely on fdopendir/readdir for portability

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    struct dirent
    {
        public ulong d_ino; // Inode number
        public ulong d_off; // Offset to the next dirent
        public ushort d_reclen; // Length of this record
        public byte d_type; // Type of file
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = NAME_MAX + 1)]
        public string d_name; // Filename (null-terminated)
    }

    //P/Invoke declarations for small set of libc directory functions
    [DllImport("libc", SetLastError = true)]
    static extern int openat(int dirfd, string pathname, int flags, uint mode = 0);
    // openat: opens a file relative to a directory file descriptor. dirfd is the file descriptor of the directory, pathname is the name of the file to open, flags specify how to open the file (e.g., O_RDONLY for read-only), and mode is used when creating a new file (not needed for directories).


    [DllImport("libc", SetLastError = true )]
    static extern IntPtr fdopendir(int fd);
    // fdopendir: converts a file descriptor to a directory stream. fd is the file descriptor obtained from openat.


    [DllImport("libc", SetLastError = true)]
    static extern IntPtr readdir(IntPtr dirp);
    // readdir: reads a directory entry from the directory stream pointed to by dirp.

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr opendir(string name);
    // opendir: opens a directory stream for the directory named by "name" and returns a DIR*


    [DllImport("libc", SetLastError = true)]
    static extern int closedir(IntPtr dirp);
    // closedir: closes the directory stream pointed to by dirp.

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);
    // close: closes a file descriptor.

    public static IEnumerable<string> ParallelSearch(string root, string targetName, bool exactMatch = true, bool folderOnly = false, bool firstMatchOnly = false, bool applicationOnly = false, int maxDegreeOfParallelism = 0)
    {
        int workers = maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount;
        var stack = new System.Collections.Concurrent.ConcurrentStack<string>();
        stack.Push(root);
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        var cts = new System.Threading.CancellationTokenSource();
        var token = cts.Token;
        int foundFlag = 0;
        string foundPath = null;

        Parallel.ForEach(Enumerable.Range(0, workers), new ParallelOptions { MaxDegreeOfParallelism = workers}, _ =>
        {
            while (!token.IsCancellationRequested && stack.TryPop(out var dir))
            {
                IntPtr dirp = opendir(dir);
                if (dirp == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    Debug.WriteLine($"opendir failed for {dir}, errno={err}");
                    continue;
                }

                try
                {
                    while(!token.IsCancellationRequested)
                    {
                        IntPtr entryPtr = readdir(dirp);
                        if (entryPtr == IntPtr.Zero)
                        {
                            int err = Marshal.GetLastWin32Error();
                            if (err != 0)
                            {
                                Debug.WriteLine($"readdir error for {dir}, errno: {err}");
                            }
                            break;
                        }

                        dirent entry = Marshal.PtrToStructure<dirent>(entryPtr);
                        string name = entry.d_name;

                        if (name == "." || name == "..")
                            continue;

                        string path = System.IO.Path.Combine(dir, name);

                        bool isDir = entry.d_type == 4; // DT_DIR
                        bool match = string.Equals(name, targetName, StringComparison.Ordinal) ||
                                     (!exactMatch && name.Contains(targetName));

                        if(folderOnly)
                        {
                            if(isDir && match)
                            {
                                results.Add(path);
                                if(firstMatchOnly && System.Threading.Interlocked.CompareExchange(ref foundFlag, 1, 0) == 0)
                                {
                                    foundPath = path;
                                    cts.Cancel();
                                    break;
                                }
                            }
                        }
                        else if(applicationOnly)
                        {
                            // For simplicity, consider files with .exe, .app, .sh extensions as applications
                            bool isApp = !isDir && (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                                                     name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ||
                                                     name.EndsWith(".sh", StringComparison.OrdinalIgnoreCase) ||
                                                     name.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase));
                            if(isApp && match)
                            {
                                results.Add(path);
                                if(firstMatchOnly && System.Threading.Interlocked.CompareExchange(ref foundFlag, 1, 0) == 0)
                                {
                                    foundPath = path;
                                    cts.Cancel();
                                    break;
                                }
                            }
                        }
                        else
                        {
                            if(match)
                            {
                                results.Add(path);
                                if(firstMatchOnly && System.Threading.Interlocked.CompareExchange(ref foundFlag, 1, 0) == 0)
                                {
                                    foundPath = path;
                                    cts.Cancel();
                                    break;
                                }
                            }
                        }

                        if(isDir)
                        {
                            stack.Push(path);
                        }
                    }
                }
                finally
                {
                    closedir(dirp);
                }
            }
        });

        if (firstMatchOnly)
        {
            if (foundPath != null)
                return new[] { foundPath };
            
            return Array.Empty<string>();
        }

        return results;

    }

    // Search yields matching file paths by traversing directories using low-level libc calls.
    public static IEnumerable<string> Search(string root, string targetName, bool exactMatch = true, bool folderOnly = false, bool firstMatchOnly = false)
    {
        Stack<string> stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            // Open the directory stream using opendir (safer and portable).
            // Using opendir avoids managing raw file descriptors and prevents
            // bad-file-descriptor (EBADF) races when converting fds to DIR*.
            IntPtr dirp = opendir(dir);
            if (dirp == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine($"opendir failed for {dir}, errno={err}");
                continue;
            }

            try
            {
                while (true)
                {
                    // Read the next directory entry using readdir
                    IntPtr entryPtr = readdir(dirp);
                    if (entryPtr == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 0)
                            Console.WriteLine($"readdir error for {dir}, errno: {err}");
                        break;
                    }

                    // marshal the dirent structure from the pointer returned by readdir
                    dirent entry = Marshal.PtrToStructure<dirent>(entryPtr);
                    string name = entry.d_name;

                    // skip current and parent directory entries
                    if (name == "." || name == "..")
                        continue;

                    string path = System.IO.Path.Combine(dir, name);

                    if (folderOnly)
                    {
                        const byte DT_DIR_LOCAL = 4;
                        if (entry.d_type == DT_DIR_LOCAL)
                        {
                            if (string.Equals(name, targetName, StringComparison.Ordinal) ||
                                (!exactMatch && name.Contains(targetName)))
                            {
                                yield return path;
                            }
                        }
                    }
                    else
                    {
                        // Check for match based on name and exactMatch flag
                        if (string.Equals(name, targetName, StringComparison.Ordinal) ||
                            (!exactMatch && name.Contains(targetName)))
                        {
                            // If firstMatchOnly is true, yield the match and stop searching further
                            if(firstMatchOnly){
                                yield return path;
                                yield break; // stop after first match if requested
                            }
                            // Print found match to console for debugging
                            Console.WriteLine($"Found match: {path}");
                            yield return path;
                        }
                    }

                    // If the entry is a directory, push it onto the stack to search later
                    const byte DT_DIR = 4;
                    if (entry.d_type == DT_DIR)
                        stack.Push(path);
                }
            }
            finally
            {
                // closedir will free DIR* and close the underlying fd
                closedir(dirp);
            }
        }
    }
}

class Program
{
    static async Task Main(string[] args)
    {
        string root = "/home/";
        var results = new List<string>();
        string query = "";
        CancellationTokenSource searchCts = null;

        // Helper to build the one-column tbale with two rows
        Func<string, IEnumerable<string>, IRenderable> build = (q, r) =>
        {
            var t = new Table().AddColumn("");
            t.HideHeaders();
            t.AddRow(new Markup($"[yellow]Search:[/] {Markup.Escape(q)}"));
            var resultsText = r.Any()
                ? string.Join("\n", r.Select(Markup.Escape))
                : "[grey][i]No results[/][/]";
            // add a second row that contains the results (Panel preserves newlines)
            t.AddRow(new Panel(resultsText) { Padding = new Padding(0, 0, 0, 0), Border = BoxBorder.Rounded });
            return t;
        };

        await AnsiConsole.Live(build(query, results))
            .StartAsync(async ctx =>
            {
                ctx.Refresh();

                while (true)
                {
                    var key = Console.ReadKey(intercept: true);

                    if (key.Key == ConsoleKey.Escape)
                    {
                        break;
                    }

                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (query.Length > 0)
                        {
                            query = query.Substring(0, query.Length - 1);
                        }
                    }
                    else if (key.Key == ConsoleKey.Enter)
                    {
                        // Do nothing on Enter for now
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        query += key.KeyChar;
                    }

                    // Cancel any ongoing search
                    searchCts?.Cancel();
                    searchCts = new CancellationTokenSource();
                    var token = searchCts.Token;

                    // Start a new search task
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(120, token).ContinueWith(_ => { }, TaskScheduler.Default); // debounce delay
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }
                        var found = new List<string>();
                        if (!string.IsNullOrEmpty(query))
                        {
                            try
                            {
                                // Use your parallel native search implementation instead of Directory.EnumerateFiles
                                found = NativeDirSearch
                                    .ParallelSearch(root, query, exactMatch: false, folderOnly: false, firstMatchOnly: false, applicationOnly: false, maxDegreeOfParallelism: Environment.ProcessorCount)
                                    .Take(20)
                                    .ToList();
                            }
                            catch (Exception) { }
                        }

                        if (!token.IsCancellationRequested)
                        {
                            results = found;
                            ctx.UpdateTarget(build(query, results));
                            ctx.Refresh();
                        }

                    }, token);
                    
                    ctx.UpdateTarget(build(query, results));
                    ctx.Refresh();
                }
            });
    }
}