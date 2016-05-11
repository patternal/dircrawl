### summary

`dircrawl` is a command line utility to recursively traverse directories,
logging file and (sub)folder information and file (MD5/SHA256) hash sums
in a format suitable for import into a relational database.

`dircrawl` has been designed for fast logging of a snapshot of a filesystem
and its contents, for example as an analytical supplement to offline backups,
and for easy import into R or a SQL database for subsequent processing.

It is a good idea to leave a trailing backslash on each passed path:
> `dicrawl C:\`

This will create a subfolder, dircrawl, from wherever it is invoked,
and within there, another subfolder with the timestamp (yymmdd.HHMMSS),
and in there, four log files: {dir,file,error,crawl}.log.

### details

The compiled dicrawl.exe executable:
  * runs from the Windows command line;
  * takes one or more paths as argument(s), placing them in a queue for processing;
  * processes each path as the root of a new directory tree;
  * walks each directory tree (traversing them inorder effectively using a stack),
    with some effort avoiding cycles (if absolute paths are given;
    no concerted effort is yet made to detect cycles involving "soft" symlinks).

When folders are walked,
  * directory information is obtained:
    file/folder dates (creation, last modification, last access)
  * file size, and (MD5 or SHA256) hash sums are computed
    (which algorithm is currently determined at compile time);
    since this slows down the walk, I've called it "crawl".

This (directory & file) info is logged into separate log files,
into a subfolder of the current directory from the command shell
where `dircrawl` was invoked:
  * dir.log   -- directory names & information
  * file.log  -- file names, sums & information
  * error.log -- error messages (e.g. filename with path was too long, could not access file...)
  * crawl.log -- log counts and statistics (total size, time, processing rate)

A unique log folder (`logDir`), a subfolder of 'dircrawl', is created
for each run using the timestamp when the executable was started
(with format 'yymmdd.HHMMSS' in local time to the nearest second);
the log files from the current crawl are placed in here.

The directory and file logs are written in a format suitable for import
into a relational database, which has been successfully used to detect:
  * filesystem anomalies,
  * duplicate (renamed) files,
  * changed/different files (with the same filename but different contents),
  * and patterns of these for reconstructing history of projects over time.

There is some flexibility in the source code to write to flat files,
tab-delimited text files, or a combination of both (fiels with space padding).

MySQL import, and MySQL & R analysis scripts might be available upon request.

### history

`dircrawl` was successfully used to detect files changed from a
last offline backup in a networked environment from a compromised host,
to characterize the attack as ransomware, and to restore the files
that had been encrypted by the ransomware (before it was turned off)
from their last good backup.

`dircrawl` was also used to detect redundancy and to reconstruct
the history of workflows with file-based artifacts.

### performance

`dircrawl` took 18.5 hours to walk a 60GB filesystem (`C:\`) in a Windows VM.

### author

Ben Ginsberg, bginsbg {AT} gmail {DOT} com

