//
// dircrawl.cs -- walk directory tree(s) and list files with their MD5 sums
//
// The compiled dicrawl.exe executable:
//   runs from the Windows command line;
//   takes one or more paths as argument(s), placing them in a queue for processing;
//   processes each path as the root of a new directory tree;
//   walks each directory tree (traversing them inorder effectively using a stack),
//     with some effort avoiding cycles (if absolute paths are given;
//     no concerted effort is yet made to detect cycles involving "soft" symlinks);
//   when folders are walked,
//     directory information is obtained:
//       file size
//       file/folder dates (creation, last modification, last access)
//     and file (MD5 or SHA256) hash sums are computed
//       (which algorithm is currently determined at compile time);
//       since this slows down the walk, I've called it "crawl".
//   This (directory & file) info is logged into separate log files,
//   into a subfolder of the current directory from the command shell
//   where dircrawl was invoked:
//     ./dircrawl/yymmdd.HHMMSS/dir.log   -- directory names & information
//     ./dircrawl/yymmdd.HHMMSS/file.log  -- file names, sums & information
//     ./dircrawl/yymmdd.HHMMSS/error.log -- error messages (e.g. filename with path was too long, could not access file...)
//     ./dircrawl/yymmdd.HHMMSS/crawl.log -- log counts and statistics (total size, time, processing rate)
//   First, a subdirectory named dircrawl is created;
//   next, a subdirectory of this (logDir) is created with the timestamp,
//   in local time to the nearest second, as directory name;
//   finally the log files from the current crawl are placed in here.
//   The directory and file logs are written in a format suitable for import
//   into a relational database, which has been successfully used to detect
//     filesystem anomalies,
//     duplicate (renamed) files,
//     changed/different files (with the same filename but different contents),
//     and patterns of these for reconstructing history of projects over time.
//   There is some flexibility in the source code to write to flat files,
//   tab-delimited text files, or a combination of both (fiels with space padding).
//
// Reducing Errors from Long (Directory+)File Names:
//   The walking generates errors (for the error log) when the path and file names
//   together exceed an operating system limit of about 280 characters.
//   If the code for walking directory trees was re-written to use a stack,
//   with suitable "push" and "pop" changing of directory, it might be possible
//   to push this limit up, by avoiding the directory names being included
//   in that limit (in the calls to System.IO.DirectoryInfo/FileInfo).
//
// history:
//   TODO!        walk directory tree(s) with a stack to minimize/avoid long file/directory name errors
//   2014-10-31   remove dirCount check, use shared (single) HashAlgorithm object
//   2014-03-31   remove file path from unavailable fileinfo output
//   2013-12-18   Added summary statistics
//   2013-11-22   Added elapsed time messages and Flush() calls to fix unflushed *Log files missing data
//   2013-11-01   Forked from dirsums.cs (major overhaul)
//                Added directory parent & level, enabled SHA (recompile with useSHA=true), revamped output format & logging
//   2013-09-11   Fixed directory listing order to use a queue rather than a stack;
//                fixed error handling for directories ending in space; ...
//   2013-07-31   Adapted from MSDN sample code:
//                http://msdn.microsoft.com/en-us/library/bb513869.aspx
//
// author: Ben Ginsberg
// email: bginsbg@gmail.com
//
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;


public class DirCrawl
{

	private static string tab = "\t";
	private static string spc = " ";
	private static string sep;
	private static string[] pathSeps = {"\\", "/"};

	private static bool tabSeperated  = true;
	private static bool justifyFields = true;
	private static bool useSHA        = false;
	private static bool crawl         = false; // whether to change directory during traversal

	// Fixed-width Field formats:       DirID    ParID    Level    ctime     mtime     atime                        name
	private static string[] dirFmt0  = {"{0}"   ,"{1}"   ,"{2}"   ,"{3}"    ,"{4}"    ,"{5}"    ,                   "{6}"};
	private static string[] dirFmt1  = {"{0,-7}","{1,-7}","{2,-5}","{3,-13}","{4,-13}","{5,-13}",                   "{6}"};

	// Fixed-width Field formats:       FileID   DirID             ctime     mtime     atime     Size     FileSum   name
	private static string[] fileFmt0 = {"{0}"   ,"{1}"            ,"{2}"    ,"{3}"    ,"{4}"    ,"{5}"   ,"{6}"    ,"{7}"};
	private static string[] fileFmt1 = {"{0,-7}","{1,-7}",         "{2,-13}","{3,-13}","{4,-13}","{5,12}","{6,-32}","{7}"};

	private static String dirFmt;
	private static String fileFmt;

	private static String na_str = "NA"; // or: "unavailable", "NULL"

	private static HashAlgorithm hasher;

	static void Main(string[] args) // Specify the starting "root" folder(s) on the command line
	{
		//ShowTimeTicksDemo(DateTime.Now);

		sep = tabSeperated ? tab : spc;

		// use a pooled HashAlgorithm for all files
		hasher = useSHA
			? (HashAlgorithm) new SHA256Managed()
			: (HashAlgorithm) new MD5Cng()
			;

		if (justifyFields)
		{
			if (useSHA) fileFmt1[6] = "{6,-64}";
			dirFmt  = String.Join(sep, dirFmt1);
			fileFmt = String.Join(sep, fileFmt1);
		}
		else
		{
			dirFmt  = String.Join(sep, dirFmt0);
			fileFmt = String.Join(sep, fileFmt0);
		}

		TraverseTree(args);
	}

	private static string GetHash(string file) // this seems fast enough
	{
		using (BufferedStream stream = new BufferedStream(File.OpenRead(file), 1200000)) //~1MB
		{
			byte[] checksum = hasher.ComputeHash(stream);
			return BitConverter.ToString(checksum).Replace("-", String.Empty);
		}
	}

	// DateTimeTo6dot6 gives 'yymmdd.hhmmss':
	// (2-digit) (year, month, day) + dot + (hour, minute, second)
	// TODO: handle return value "001231.160000" occuring under certain error scenarios, e.g. directory name ending in space.
	public static string DateTimeTo6dot6(DateTime dt)
	{
		return String.Format(
			"{0:yy}{0:MM}{0:dd}.{0:HH}{0:mm}{0:ss}" // + "{0:fff}"
			, dt
		);
	}

	// write line to console and 'tee' to file (e.g. opened in append mode, which client should ultimately flush).
	public static void Tee(TextWriter w, string fmt, params object[] args)
	{
		string s = String.Format(fmt, args);
		Console.WriteLine(s);
		w.WriteLine(s);
	}

	public static void PrintDirHeader(TextWriter w)
	{
		string[] fieldNames = // directory ID, parent ID, level under root, ...
			{"DirId", "ParId", "Level", "FirstWrite", "LastWrite", "LastRead", "DirName"};
		w.WriteLine(dirFmt, fieldNames);
	}

	public static void PrintFileHeader(TextWriter w)
	{
		string[] fieldNames = {"FileId", "DirId", "FirstWrite", "LastWrite", "LastRead", "FileSize", "FileSum", "FileName"};
		w.WriteLine(fileFmt, fieldNames);
	}

	public static void PrintDirInfo(TextWriter wout, TextWriter werr, string path, int folderId, int parentId, int level)
	{
		bool caughtError = false;
		String errMessage = "";

		try
		{
			System.IO.DirectoryInfo di = new System.IO.DirectoryInfo(path);
			wout.WriteLine(dirFmt
				, folderId
				, parentId
				, level
				, DateTimeTo6dot6(di.CreationTime)
				, DateTimeTo6dot6(di.LastWriteTime)
				, DateTimeTo6dot6(di.LastAccessTime)
				, di.Name
			);
		}
		catch (UnauthorizedAccessException e)
		{
			caughtError = true;
			errMessage = e.Message;
		}
		catch (System.IO.IOException e)
		{
			// If file can't be accessed, e.g. because it is locked by another process, report & continue.
			// Also includes System.IO.FileNotFoundException in case file was deleted by another thread/process.
			caughtError = true;
			errMessage = e.Message;
		}
		catch (Exception e)
		{
			caughtError = true;
			errMessage = e.Message;
		}
		if (caughtError)
		{
			werr.WriteLine("Folder {0}, {1}: {2}", folderId, path, errMessage);
			wout.WriteLine(dirFmt, folderId, parentId, level, na_str, na_str, na_str, path);
		}
	}

	public static long PrintFileInfo(TextWriter wout, TextWriter werr, string path, int fileId, int dirId)
	{
		long fileSize = -1;
		bool caughtError = false;
		String errMessage = "";
		try
		{
			System.IO.FileInfo fi = new System.IO.FileInfo(path);
			wout.WriteLine(fileFmt
				, fileId
				, dirId
				, DateTimeTo6dot6(fi.CreationTime)
				, DateTimeTo6dot6(fi.LastWriteTime)
				, DateTimeTo6dot6(fi.LastAccessTime)
				, fileSize = fi.Length
			//	, useSHA ? GetSHA(fi.FullName)
			//	         : GetMD5(fi.FullName)
				, GetHash(fi.FullName)
				, fi.Name
			);
		}
		catch (UnauthorizedAccessException e)
		{
			caughtError = true;
			errMessage = e.Message;
		}
		catch (System.IO.IOException e)
		{
			// If file can't be accessed, e.g. because it is locked by another process, report & continue.
			// Also includes System.IO.FileNotFoundException in case file was deleted by another thread/process.
			caughtError = true;
			errMessage = e.Message;
		}
		catch (Exception e)
		{
			caughtError = true;
			errMessage = e.Message;
		}
		if (caughtError) {
			string filename = path;
			try {
				List<string> s = new List<string>(path.Split(pathSeps, StringSplitOptions.RemoveEmptyEntries));
				filename = s[s.Count-1];
			}
			catch {}
			werr.WriteLine("File {0}, {1}: {2}", fileId, path, errMessage);
			wout.WriteLine(
				fileFmt, fileId, dirId,
				na_str, na_str, na_str, na_str, na_str, // ctime, mtime, atime, Size, fileSum
				filename
			);
		}
		return fileSize;
	}

	public static string ComputeRelativePath(String from, String to)
	{
		List<string> a = new List<string>(from.Split(pathSeps, StringSplitOptions.RemoveEmptyEntries));
		List<string> b = new List<string>(  to.Split(pathSeps, StringSplitOptions.RemoveEmptyEntries));
		while (a.Count>0
				&& b.Count>0
				&& a[0].Equals(b[0], StringComparison.InvariantCultureIgnoreCase))
		{
			a.RemoveAt(0);
			b.RemoveAt(0);
		}
		for (int i=0; i<a.Count; i++) a[i] = "..";
		string relPath = String.Join("\\", a.ToArray());
		return relPath + String.Join("\\", b.ToArray());
	}

	// TODO: return string[], to join with " = " if desired
	public static string MetricEquivalencies(double x)
	{
		string str = "";
		char[] trailingZero = {'0'};
		char[] decimalPoint = {'.'};
		string[] metricSuffix = {"K","M","G","T"};
		// show in all larger units above threshold value (0.01)
		for (int i=0; i<4 && Math.Abs(x/=1024) >= 0.01; i++) // K, M, G, T
		{
			String s = String.Format(" = {0:0.00}", x);
			s = s.TrimEnd(trailingZero);
			s = s.TrimEnd(decimalPoint);
			str += s + metricSuffix[i];
		}
		return str;
	}

	public static bool FindOrMakeDir(string path)
	{
		if (Directory.Exists(path))
		{
			Console.WriteLine("{0,-20}: {1}", "using log folder", path);
			return true;
		}
		try
		{
			DirectoryInfo di = Directory.CreateDirectory(path);
			Console.WriteLine("{0,-20}: {1}", "created log folder", path);
		}
		catch (Exception e)
		{
			Console.WriteLine("Could not create directory {0}: {1}", path, e.ToString());
			return false;
		}
		finally {}
		return true;
	}

	// TraverseTree walks a directory tree (preorder traversal), listing files
	// and pushing child directories onto a private (static) stack for queueing
	// to simulate recursion.
	// No attempt is made to avoid cycles caused by symbolic links.
	public static void TraverseTree(string[] roots)
	{
		List<string> queue = new List<string>(9);    // queue of directory tree roots to traverse (feeds dir)
		List<string> cdirs = new List<string>(40);   // current directory stack
		List<string> dir   = new List<string>(1000); // list of directories traversed
		List<int>    lev   = new List<int>();        // directory level
		List<string> qName = new List<string>();     // names & order of counts to display at the ending summary

		Dictionary<string,long> qCount = new Dictionary<string,long>(); // stats to display at the ending summary
		Dictionary<string,int>  dirNum = new Dictionary<string,int>();  // unique number and index into dir
		Dictionary<int,int>     dirPar = new Dictionary<int,int>();     // dir index => parent dirNum index + 1
		Dictionary<string,bool> isDir  = new Dictionary<string,bool>(); // whether path is a directory (false => file)
		Dictionary<string,bool> notDir = new Dictionary<string,bool>(1);// directories to exclude (key existence implies true; bool used since void not allowed)

		DateTime t0, t1; // starting and ending times

		string startDir  = Directory.GetCurrentDirectory(); // necessary?: startDir = DirectoryInfo(startDir).FullName();
		string startTime = DateTimeTo6dot6(t0 = DateTime.Now);
		string logDir    = String.Format(@"{0}\dircrawl\{1}", startDir, startTime); // this should always be a new directory unless OS is time traveling!

    //logDir = logDir.ToLower();
    //Console.WriteLine("logDir = {0}", logDir);

		if (!FindOrMakeDir(logDir)) return; // use existing directory if for some reason it already exists
		notDir.Add(logDir.ToLower(), true); // exclude the logfile directory from traversal
		Console.WriteLine("dircrawl started from {0}", startDir);
		Console.WriteLine("dircrawl start  time: {0}", startTime); // cf. {end,elapsed}Time messages at end of TraverseTree

		// construct log file names and open file streams for appending (even though they should all be new files)
		StreamWriter dirLog  = File.AppendText(String.Format(@"{0}\dir.log"  , logDir));
		StreamWriter fileLog = File.AppendText(String.Format(@"{0}\file.log" , logDir));
		StreamWriter errLog  = File.AppendText(String.Format(@"{0}\error.log", logDir));
		StreamWriter actLog  = File.AppendText(String.Format(@"{0}\crawl.log", logDir));

		PrintDirHeader(dirLog);
		PrintFileHeader(fileLog);

		// initialize summary counts
		qName.Add("distinct files");
		qName.Add("distinct directories");
		qName.Add("child directories");
		qName.Add("skipped directories");
		qName.Add("leaf directories"); // i.e./aka childless, bottom directories
		qName.Add("file list error");
		qName.Add("subfolder list error");
		qName.Add("dir permission error");
		qName.Add("dir not found error");
		qName.Add("bytes processed");
		qName.Add("processing speed B/s");
		foreach (string s in qName) qCount.Add(s,0);

		// add passed arguments to directory queue
		int i = 0;
		foreach (string root in roots)
		{
			bool good = System.IO.Directory.Exists(root);
			string path = good ? (new System.IO.DirectoryInfo(root)).FullName : root;
			if (good) queue.Add(path); // add a potential root folder to the queue
			else errLog.WriteLine(">> Directory not found (skipping): {0}", root);
			actLog.WriteLine("dircrawl root path {0}: {1}{2}", ++i, good?"":"*", path);
		}

		actLog.WriteLine("dircrawl start  time: {0}", startTime); // cf. {end,elapsed}Time messages at end of TraverseTree

		int dirErrors = 0;
		int dirCount, fileCount, argCount, level, parentId = 0;
		string lcpath, currentDir = "";

		// continue as long as:
		// either  1. the number of discovered (found) directories exceeds the number processed ( dir.Count > dirCount )
		// or      2. the number of requested (root) directories exceeds the number processed ( queue.Count > argCount )
		for (dirCount=0, fileCount=0, argCount=0; dirCount < dir.Count || argCount < queue.Count; dirCount++)
		{
			if (dir.Count <= dirCount) // directory processing queue is empty; ergo: argCount < queue.Count
			{
				dir.Add(queue[argCount++]); // start next queued root
				lev.Add(0);
				cdirs.Clear();
			}

			if (crawl)
			{
				try
				{
					string relPath = dir[dirCount];
					if (lev[dirCount] > 0) relPath = ComputeRelativePath(currentDir, relPath);
					string[] paths = relPath.Split(pathSeps, StringSplitOptions.RemoveEmptyEntries);
					foreach (string s in paths) Directory.SetCurrentDirectory(s);
				}
				catch (Exception) {
					Tee(actLog, "Warning: failed to change to directory\nfrom:\t{0}\nto:\t{1}", currentDir, dir[dirCount]);
					dirErrors++;
				}
			}

			currentDir = dir[dirCount];
			level      = lev[dirCount];
			parentId   = dirPar.ContainsKey(dirCount) ? dirPar[dirCount] : 0;
			lcpath     = currentDir.ToLower();

			// guard against cycles, assuming case-insensitive filesystem
			if (dirNum.ContainsKey(lcpath))
			{
				errLog.WriteLine(">> Cycle encountered! Skipping redundant traversal of dir {0}={1}: {2}",
					dirCount, dirNum[lcpath], currentDir);
				continue;
			}
			else // assign each directory a unique nonnegative number, namely the order (first) traversed
			{
				dirNum[lcpath] = 0; // adds a key to affect dirNum.Count
				dirNum[lcpath] = dirNum.Count;
				PrintDirInfo(dirLog, errLog, currentDir, dirNum.Count, parentId, level);
			}

			string[] subDirs;
			try
			{
				subDirs = System.IO.Directory.GetDirectories(crawl ? "." : currentDir);
			}
			// An UnauthorizedAccessException exception will be thrown if we do not have
			// discovery permission on a folder or file. It may or may not be acceptable
			// to ignore the exception and continue enumerating the remaining files and
			// folders. It is also possible (but unlikely) that a DirectoryNotFound exception
			// will be raised. This will happen if currentDir has been deleted by
			// another application or thread after our call to Directory.Exists. The
			// choice of which exceptions to catch depends entirely on the specific task
			// you are intending to perform and also on how much you know with certainty
			// about the systems on which this code will run.
			catch (UnauthorizedAccessException e)
			{
				errLog.WriteLine(">> Directory Subfolder Listing Access Error: {0}\n{1}", e.Message, currentDir);
				continue;
			}
			catch (System.IO.DirectoryNotFoundException e)
			{
				// WriteLine("..{0}..", ..) with currentDir or e.Message throws System.FormatException
				// for directory names ending with space; hence no format string used for this case:
				//    System.Text.StringBuilder.AppendFormat(IFormatProvider provider, String format, Object[] args)
				//    System.String.Format(IFormatProvider provider, String format, Object[] args)
				//    System.IO.TextWriter.WriteLine(String format, Object arg0, Object arg1)
				//    System.IO.TextWriter.SyncTextWriter.WriteLine(String format, Object arg0, Object arg1)
				//    System.Console.WriteLine(String format, Object arg0, Object arg1)
				//    DirCrawl.TraverseTree(String[] roots)
				//    DirCrawl.Main(String[] args)
				errLog.WriteLine(">> Directory Not Found:");
				errLog.WriteLine("> '"+currentDir+"'");
				errLog.WriteLine("> "+e.Message);
				continue;
			}
			catch (System.IO.IOException e)
			{
				errLog.WriteLine(">> Directory Subfolder Listing Access Error: {0}\n{1}", e.Message, currentDir);
				qCount["subfolder list error"]++;
				continue;
			}

			string[] files = null;
			try
			{
				files = System.IO.Directory.GetFiles(crawl ? "." : currentDir);
			}
			catch (UnauthorizedAccessException e)
			{
				errLog.WriteLine(">> Directory File Listing Access Error: {0}\n{1}", e.Message, currentDir);
				qCount["dir permission error"]++;
				continue;
			}
			catch (System.IO.DirectoryNotFoundException e)
			{
				errLog.WriteLine(">> Directory Not Found! {0}\n{1}", e.Message, currentDir);
				qCount["dir not found error"]++;
				continue;
			}
			catch (System.IO.IOException e)
			{
				errLog.WriteLine(">> Directory File Listing Error: {0}\n{1}", e.Message, currentDir);
				qCount["file list error"]++;
				continue;
			}

			// Perform the required action on each file here.
			// Modify this block to perform your required task.
			foreach (string file in files)
			{
				long fileSize = PrintFileInfo(fileLog, errLog, file, ++fileCount, dirNum.Count);
				if (fileSize > 0) qCount["bytes processed"] += fileSize;
			}

			// Add the subdirectories to the queue for traversal.
			// This could also be done before handing the files.
			foreach (string str in subDirs)
			{
				// skip excluded directories (e.g. the current log directory)
				//string child = lcpath + "\\" + str.ToLower(); //141031 bug: lcpath+@"\"+
				string child = str.ToLower();
				if (notDir.ContainsKey(child))
				{
					qCount["skipped directories"]++;
					continue;
				}
				else
				{
					dir.Add(str);
					//dir.Add(crawl ? (currentDir + "\\" + str) : str); //TODO: make sure we don't need this
					lev.Add(level+1);
					dirPar[dir.Count - 1] = dirNum.Count; // assign external parent dir ID to added dir
					qCount["child directories"]++;
				}
			}

			if (subDirs.Length == 0) qCount["leaf directories"]++;

		} // dircrawl main loop (dirCount)

		qCount["distinct directories"] = dir.Count;
		qCount["distinct files"] = fileCount;

		string endTime = DateTimeTo6dot6(t1 = DateTime.Now);
		TimeSpan tsElapsed = t1 - t0;
		List<double> elapsedTime = new List<double>(4);
		elapsedTime.Add( tsElapsed.TotalSeconds );
		elapsedTime.Add( elapsedTime[0] / 60 ); // minutes
		elapsedTime.Add( elapsedTime[1] / 60 ); // hours
		elapsedTime.Add( elapsedTime[2] / 24 ); // days

		Tee(actLog, "dircrawl end    time: {0}", endTime);
		Tee(actLog, "dircrawl elapsed sec: {0}", elapsedTime[0]);
		Tee(actLog, "dircrawl elapsed min: {0}", elapsedTime[1]);
		Tee(actLog, "dircrawl elapsed  hr: {0}", elapsedTime[2]);
		Tee(actLog, "dircrawl elapsed day: {0}", elapsedTime[3]);

		double dist = qCount["bytes processed"];
		double rate = elapsedTime[0];
		if (rate > 0) rate = dist / rate;
		qCount["processing speed B/s"] = (long) rate;

		foreach (KeyValuePair<string,long> kv in qCount)
		{
			String str = String.Format("{0,-20}: {1}", kv.Key, kv.Value);

			// show alternate units on same line for total bytes processed & processing rate
			if (str.StartsWith("bytes processed"))
			{
				str += MetricEquivalencies(dist);
			}
			if (str.StartsWith("processing speed")) // re-format, as double
			{
				str = String.Format("{0,-20}: {1}", kv.Key, rate)
						+ MetricEquivalencies(rate);
			}

			Tee(actLog, str);
		}

		dirLog.Flush();
		fileLog.Flush();
		errLog.Flush();
		actLog.Flush();

	} // method TraverseTree

} // class DirCrawl

