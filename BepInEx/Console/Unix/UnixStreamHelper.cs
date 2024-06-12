﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace BepInEx.Unix
{
	internal static class UnixStreamHelper
	{
		private static IntPtr libcHandle;

		public delegate int dupDelegate(int fd);
		public static dupDelegate dup;

		public delegate IntPtr fdopenDelegate(int fd, string mode);
		public static fdopenDelegate fdopen;

		public delegate IntPtr freadDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);
		public static freadDelegate fread;

		public delegate int fwriteDelegate(IntPtr ptr, IntPtr size, IntPtr nmemb, IntPtr stream);
		public static fwriteDelegate fwrite;

		public delegate int fcloseDelegate(IntPtr stream);
		public static fcloseDelegate fclose;

		public delegate int fflushDelegate(IntPtr stream);
		public static fflushDelegate fflush;

		public delegate int isattyDelegate(int fd);
		public static isattyDelegate isatty;

		static UnixStreamHelper()
		{
			libcHandle = DynDll.OpenLibrary(PlatformDetection.OS.Is(OSKind.OSX) ? "/usr/lib/libSystem.dylib" : "libc");
			dup = AsDelegate<dupDelegate>(libcHandle.GetExport("dup"));
			fdopen = AsDelegate<fdopenDelegate>(libcHandle.GetExport("fdopen"));
			fread = AsDelegate<freadDelegate>(libcHandle.GetExport("fread"));
			fwrite = AsDelegate<fwriteDelegate>(libcHandle.GetExport("fwrite"));
			fclose = AsDelegate<fcloseDelegate>(libcHandle.GetExport("fclose"));
			fflush = AsDelegate<fflushDelegate>(libcHandle.GetExport("fflush"));
			isatty = AsDelegate<isattyDelegate>(libcHandle.GetExport("isatty"));
		}

		private static T AsDelegate<T>(IntPtr s) where T : class => Marshal.GetDelegateForFunctionPointer(s, typeof(T)) as T;

		public static Stream CreateDuplicateStream(int fileDescriptor)
		{
			int newFd = dup(fileDescriptor);

			return new UnixStream(newFd, FileAccess.Write);
		}
	}
}