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

    // Search yields matching file paths by traversing directories using low-level libc calls.
    public static IEnumerable<string> Search(string root, string targetName, bool exactMatch = true)
    {
        int filesSearched = 0;
        Stack<string> stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            // open the directory using opendir (simpler than openat+fdopendir)
            IntPtr dirp = opendir(dir);
            if (dirp == IntPtr.Zero)
            {
                Console.WriteLine($"Failed to open directory: {dir}, error: {Marshal.GetLastWin32Error()}");
                continue; // skip if opendir fails
            }

            try
            {
                Debug.WriteLine($"Opened directory stream: dirp=0x{dirp.ToString("x")}");
                while (true)
                {
                    Debug.WriteLine($"Reading directory: {dir}");
                    IntPtr entryPtr = readdir(dirp);
                    if (entryPtr == IntPtr.Zero)
                    {
                        int err = Marshal.GetLastWin32Error();
                        if (err != 0)
                        {
                            Debug.WriteLine($"readdir error for {dir}, errno: {err}");
                        }
                        else
                        {
                            Debug.WriteLine($"Finished reading directory: {dir}");
                        }
                        break; // no more entries or error
                    }

                    // marshal the dirent structure from the pointer
                    dirent entry = Marshal.PtrToStructure<dirent>(entryPtr);
                    string name = entry.d_name;

                    // skip current and parent directory entries
                    if (entry.d_name == "." || entry.d_name == "..")
                    {
                        Debug.WriteLine($"Skipping entry: {name}");
                        continue;
                    }

                    // build full path for the entry
                    string path = System.IO.Path.Combine(dir, name);

                    // if the filename matches targetName exactly, yield the path
                    if (string.Equals(name, targetName, StringComparison.Ordinal))
                    {
                        Console.WriteLine($"Found match: {path}");
                        filesSearched++;
                        yield return path;
                    }
                    else if (!exactMatch && name.Contains(targetName))
                    {
                        Console.WriteLine($"Found partial match: {path}");
                        yield return path;
                        filesSearched++;
                    }else{
                        filesSearched++;
                    }
                    // d_ytpe == 4 (DT_DIR) usually indicates a directory entry on linux
                    const byte DT_DIR = 4;
                    if (entry.d_type == DT_DIR)
                    {
                        stack.Push(path); // add subdirectory to stack for further searching
                    }
                }
            }
            finally
            {
                closedir(dirp); // ensure the directory stream is closed to free resources
                Debug.WriteLine($"Closed directory: {dir}, total files searched so far: {filesSearched}");
            }
        }

    }
}

class Program
{
    static void Main(string[] args)
    {
        string root = args.Length > 0 ? args[0] : "/home/leon/Documents/";
        string target = args.Length > 1 ? args[1] : "test.txt";
        Console.Error.WriteLine($"Searching for '{target}' starting from '{root}'...");

        var results = new System.Collections.Generic.List<string>();
        foreach (var p in NativeDirSearch.Search(root, target))
            results.Add(p);

        if (results.Count == 0)
        {
            Console.WriteLine("No matches found");
        }
        else
        {
            foreach (var p in results) Console.WriteLine(p);
        }
    }
}