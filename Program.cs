namespace TerminalSearch;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;

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
    static void Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "/home/leon/";
        string target = args.Length > 1 ? args[1] : "test.txt";
        Console.Error.WriteLine($"Searching for '{target}' starting from '{root}'...");

        var results = new System.Collections.Generic.List<string>();
        foreach (var p in NativeDirSearch.Search(root, target, true, false, true))
            results.Add(p);

        if (results.Count == 0)
        {
            Console.WriteLine("No matches found");
        }
        else
        {
            foreach (var p in results) Console.WriteLine(p);
            Console.WriteLine($"Found {results.Count} matches");
            //print time taken for search
             Console.WriteLine($"Search completed in {DateTime.Now - Process.GetCurrentProcess().StartTime}");
        }
    }
}