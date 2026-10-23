// Browser shim for System.IO.IsolatedStorage. See the .csproj for why this exists.
//
// What .NET does on Linux and macOS, done over the page's file system: a store is a folder, under the
// user's local application data (machine scope: the common application data; roaming: the roaming
// one), named for the scope and the identities that select it, and every path the application gives is
// inside that folder. The identities are the entry assembly's name for the assembly, domain and
// application alike -- a page runs one application in one domain.
//
// The page's file system lives as long as the page: what is stored survives the session, not a reload.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace System.IO.IsolatedStorage
{
    [Flags]
    public enum IsolatedStorageScope
    {
        None = 0,
        User = 1,
        Domain = 2,
        Assembly = 4,
        Roaming = 8,
        Machine = 16,
        Application = 32,
    }

    public interface INormalizeForIsolatedStorage
    {
        object Normalize();
    }

    public class IsolatedStorageException : Exception
    {
        public IsolatedStorageException() : base("An error occurred with isolated storage.") { }
        public IsolatedStorageException(string message) : base(message) { }
        public IsolatedStorageException(string message, Exception inner) : base(message, inner) { }
        [Obsolete("This API supports obsolete formatter-based serialization. It should not be called or extended by application code.", DiagnosticId = "SYSLIB0051", UrlFormat = "https://aka.ms/dotnet-warnings/{0}")]
        protected IsolatedStorageException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context) : base(info, context) { }
    }

    public abstract class IsolatedStorage : MarshalByRefObject
    {
        private object _applicationIdentity, _assemblyIdentity, _domainIdentity;
        private IsolatedStorageScope _scope;

        protected IsolatedStorage() { }

        public object ApplicationIdentity => (_scope & IsolatedStorageScope.Application) != 0 ? _applicationIdentity : throw new InvalidOperationException("The application identity is not available for this store's scope.");
        public object AssemblyIdentity => (_scope & IsolatedStorageScope.Assembly) != 0 ? _assemblyIdentity : throw new InvalidOperationException("The assembly identity is not available for this store's scope.");
        public object DomainIdentity => (_scope & IsolatedStorageScope.Domain) != 0 ? _domainIdentity : throw new InvalidOperationException("The domain identity is not available for this store's scope.");

        [CLSCompliant(false)]
        [Obsolete("IsolatedStorage.CurrentSize has been deprecated because it is not CLS Compliant. To get the current size use IsolatedStorage.UsedSize instead.")]
        public virtual ulong CurrentSize => throw new InvalidOperationException("This store does not report a size.");

        [CLSCompliant(false)]
        [Obsolete("IsolatedStorage.MaximumSize has been deprecated because it is not CLS Compliant. To get the maximum size use IsolatedStorage.Quota instead.")]
        public virtual ulong MaximumSize => throw new InvalidOperationException("This store does not report a size.");

        public virtual long AvailableFreeSpace => throw new InvalidOperationException("This store does not report its free space.");
        public virtual long Quota => throw new InvalidOperationException("This store has no quota.");
        public virtual long UsedSize => throw new InvalidOperationException("This store does not report a size.");
        public IsolatedStorageScope Scope => _scope;

        protected virtual char SeparatorExternal => Path.DirectorySeparatorChar;
        protected virtual char SeparatorInternal => '.';

        public virtual bool IncreaseQuotaTo(long newQuotaSize) => false;

        public abstract void Remove();

        protected void InitStore(IsolatedStorageScope scope, Type appEvidenceType) => InitStore(scope, null, null);

        protected void InitStore(IsolatedStorageScope scope, Type domainEvidenceType, Type assemblyEvidenceType)
        {
            IsolatedStorageFile.Validate(scope);
            _scope = scope;
            string identity = Identity();
            _applicationIdentity = identity;
            _assemblyIdentity = identity;
            _domainIdentity = identity;
        }

        internal static string Identity()
        {
            Assembly entry = Assembly.GetEntryAssembly();
            return entry?.GetName().Name ?? AppDomain.CurrentDomain.FriendlyName ?? "app";
        }
    }

    public sealed class IsolatedStorageFile : IsolatedStorage, IDisposable
    {
        private readonly string _root;
        private bool _closed, _disposed;

        private IsolatedStorageFile(IsolatedStorageScope scope)
        {
            InitStore(scope, null, null);
            _root = RootFor(scope, Identity());
            Directory.CreateDirectory(_root);
        }

        internal string RootDirectory => _root;

        public static bool IsEnabled => true;

        public override long AvailableFreeSpace => long.MaxValue;
        public override long Quota => long.MaxValue;
        public override long UsedSize { get { EnsureOpen(); return Size(_root); } }

        [CLSCompliant(false)]
        [Obsolete("IsolatedStorage.CurrentSize has been deprecated because it is not CLS Compliant. To get the current size use IsolatedStorage.UsedSize instead.")]
        public override ulong CurrentSize => (ulong)UsedSize;

        [CLSCompliant(false)]
        [Obsolete("IsolatedStorage.MaximumSize has been deprecated because it is not CLS Compliant. To get the maximum size use IsolatedStorage.Quota instead.")]
        public override ulong MaximumSize => long.MaxValue;

        public override bool IncreaseQuotaTo(long newQuotaSize) => true;

        // ---- the stores ------------------------------------------------------------------------

        public static IsolatedStorageFile GetUserStoreForApplication() => new IsolatedStorageFile(IsolatedStorageScope.User | IsolatedStorageScope.Application);
        public static IsolatedStorageFile GetUserStoreForAssembly() => new IsolatedStorageFile(IsolatedStorageScope.User | IsolatedStorageScope.Assembly);
        public static IsolatedStorageFile GetUserStoreForDomain() => new IsolatedStorageFile(IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly);
        public static IsolatedStorageFile GetUserStoreForSite() => throw new NotSupportedException("IsolatedStorageFile.GetUserStoreForSite() is not supported.");
        public static IsolatedStorageFile GetMachineStoreForApplication() => new IsolatedStorageFile(IsolatedStorageScope.Machine | IsolatedStorageScope.Application);
        public static IsolatedStorageFile GetMachineStoreForAssembly() => new IsolatedStorageFile(IsolatedStorageScope.Machine | IsolatedStorageScope.Assembly);
        public static IsolatedStorageFile GetMachineStoreForDomain() => new IsolatedStorageFile(IsolatedStorageScope.Machine | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly);

        public static IsolatedStorageFile GetStore(IsolatedStorageScope scope, Type applicationEvidenceType) => new IsolatedStorageFile(scope);
        public static IsolatedStorageFile GetStore(IsolatedStorageScope scope, object applicationIdentity) => new IsolatedStorageFile(scope);
        public static IsolatedStorageFile GetStore(IsolatedStorageScope scope, Type domainEvidenceType, Type assemblyEvidenceType) => new IsolatedStorageFile(scope);
        public static IsolatedStorageFile GetStore(IsolatedStorageScope scope, object domainIdentity, object assemblyIdentity) => new IsolatedStorageFile(scope);

        public static IEnumerator GetEnumerator(IsolatedStorageScope scope)
        {
            var stores = new List<IsolatedStorageFile>();
            foreach (IsolatedStorageScope s in new[]
            {
                scope | IsolatedStorageScope.Assembly,
                scope | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly,
                scope | IsolatedStorageScope.Application,
            })
            {
                if (IsValid(s) && Directory.Exists(RootFor(s, Identity()))) stores.Add(new IsolatedStorageFile(s));
            }
            return stores.GetEnumerator();
        }

        public static void Remove(IsolatedStorageScope scope)
        {
            string top = ScopeRoot(scope);
            if (Directory.Exists(top)) Directory.Delete(top, recursive: true);
        }

        public override void Remove()
        {
            Close();
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        public void Close() => _closed = true;

        public void Dispose()
        {
            Close();
            _disposed = true;
        }

        // ---- files and folders in the store ------------------------------------------------------

        public void CreateDirectory(string dir)
        {
            ArgumentNullException.ThrowIfNull(dir);
            Guard(() => Directory.CreateDirectory(Full(dir)), "Unable to create directory.");
        }

        public void DeleteDirectory(string dir)
        {
            ArgumentNullException.ThrowIfNull(dir);
            Guard(() => Directory.Delete(Full(dir), recursive: false), "Unable to delete directory.");
        }

        public void DeleteFile(string file)
        {
            ArgumentNullException.ThrowIfNull(file);
            Guard(() => File.Delete(Full(file)), "Unable to delete file.");
        }

        public bool DirectoryExists(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            EnsureOpen();
            return Directory.Exists(Full(path));
        }

        public bool FileExists(string path)
        {
            ArgumentNullException.ThrowIfNull(path);
            EnsureOpen();
            return File.Exists(Full(path));
        }

        public string[] GetDirectoryNames() => GetDirectoryNames("*");

        public string[] GetDirectoryNames(string searchPattern)
        {
            ArgumentNullException.ThrowIfNull(searchPattern);
            EnsureOpen();
            (string folder, string pattern) = Split(searchPattern);
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Names(Directory.GetDirectories(folder, pattern));
        }

        public string[] GetFileNames() => GetFileNames("*");

        public string[] GetFileNames(string searchPattern)
        {
            ArgumentNullException.ThrowIfNull(searchPattern);
            EnsureOpen();
            (string folder, string pattern) = Split(searchPattern);
            if (!Directory.Exists(folder)) return Array.Empty<string>();
            return Names(Directory.GetFiles(folder, pattern));
        }

        public DateTimeOffset GetCreationTime(string path) => Time(path, File.GetCreationTime);
        public DateTimeOffset GetLastAccessTime(string path) => Time(path, File.GetLastAccessTime);
        public DateTimeOffset GetLastWriteTime(string path) => Time(path, File.GetLastWriteTime);

        public void CopyFile(string sourceFileName, string destinationFileName) => CopyFile(sourceFileName, destinationFileName, false);

        public void CopyFile(string sourceFileName, string destinationFileName, bool overwrite)
        {
            ArgumentNullException.ThrowIfNull(sourceFileName);
            ArgumentNullException.ThrowIfNull(destinationFileName);
            Guard(() => File.Copy(Full(sourceFileName), Full(destinationFileName), overwrite), "Operation not permitted.");
        }

        public void MoveFile(string sourceFileName, string destinationFileName)
        {
            ArgumentNullException.ThrowIfNull(sourceFileName);
            ArgumentNullException.ThrowIfNull(destinationFileName);
            Guard(() => File.Move(Full(sourceFileName), Full(destinationFileName)), "Operation not permitted.");
        }

        public void MoveDirectory(string sourceDirectoryName, string destinationDirectoryName)
        {
            ArgumentNullException.ThrowIfNull(sourceDirectoryName);
            ArgumentNullException.ThrowIfNull(destinationDirectoryName);
            Guard(() => Directory.Move(Full(sourceDirectoryName), Full(destinationDirectoryName)), "Operation not permitted.");
        }

        public IsolatedStorageFileStream CreateFile(string path) => new IsolatedStorageFileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, this);
        public IsolatedStorageFileStream OpenFile(string path, FileMode mode) => new IsolatedStorageFileStream(path, mode, this);
        public IsolatedStorageFileStream OpenFile(string path, FileMode mode, FileAccess access) => new IsolatedStorageFileStream(path, mode, access, this);
        public IsolatedStorageFileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share) => new IsolatedStorageFileStream(path, mode, access, share, this);

        // ---- the store's folder --------------------------------------------------------------------

        internal static void Validate(IsolatedStorageScope scope)
        {
            if (!IsValid(scope)) throw new ArgumentException("The scope is not valid.", nameof(scope));
        }

        private static bool IsValid(IsolatedStorageScope scope) => scope switch
        {
            IsolatedStorageScope.User | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.Roaming | IsolatedStorageScope.User | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.Roaming | IsolatedStorageScope.User | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.Machine | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.Machine | IsolatedStorageScope.Domain | IsolatedStorageScope.Assembly => true,
            IsolatedStorageScope.Application | IsolatedStorageScope.User => true,
            IsolatedStorageScope.Application | IsolatedStorageScope.User | IsolatedStorageScope.Roaming => true,
            IsolatedStorageScope.Application | IsolatedStorageScope.Machine => true,
            _ => false,
        };

        // where a scope's stores are: the user's local application data, the roaming one, or the
        // machine's common application data -- each with an IsolatedStorage folder of its own
        private static string ScopeRoot(IsolatedStorageScope scope)
        {
            Environment.SpecialFolder folder = (scope & IsolatedStorageScope.Machine) != 0 ? Environment.SpecialFolder.CommonApplicationData
                : (scope & IsolatedStorageScope.Roaming) != 0 ? Environment.SpecialFolder.ApplicationData
                : Environment.SpecialFolder.LocalApplicationData;
            string basePath = Environment.GetFolderPath(folder, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(basePath)) basePath = Path.Combine(Path.GetTempPath(), folder.ToString());
            return Path.Combine(basePath, "IsolatedStorage");
        }

        private static string RootFor(IsolatedStorageScope scope, string identity)
        {
            string kind = (scope & IsolatedStorageScope.Application) != 0 ? "AppFiles"
                : (scope & IsolatedStorageScope.Domain) != 0 ? "DomainFiles" : "AssemFiles";
            return Path.Combine(ScopeRoot(scope), Hash(identity), kind);
        }

        private static string Hash(string identity)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            var sb = new StringBuilder(24);
            for (int i = 0; i < 12; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        /// <summary>A path the application gives, inside the store; one that leaves it is refused, as .NET
        /// refuses it.</summary>
        internal string Full(string path)
        {
            EnsureOpen();
            string full = Path.GetFullPath(Path.Combine(_root, path.TrimStart('/', '\\')));
            string root = Path.GetFullPath(_root);
            if (!full.Equals(root, StringComparison.Ordinal) && !full.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new IsolatedStorageException("Operation not permitted.");
            return full;
        }

        private (string Folder, string Pattern) Split(string searchPattern)
        {
            string dir = Path.GetDirectoryName(searchPattern);
            string pattern = Path.GetFileName(searchPattern);
            return (string.IsNullOrEmpty(dir) ? _root : Full(dir), string.IsNullOrEmpty(pattern) ? "*" : pattern);
        }

        private static string[] Names(string[] paths)
        {
            var names = new string[paths.Length];
            for (int i = 0; i < paths.Length; i++) names[i] = Path.GetFileName(paths[i]);
            return names;
        }

        private DateTimeOffset Time(string path, Func<string, DateTime> get)
        {
            ArgumentNullException.ThrowIfNull(path);
            try { return new DateTimeOffset(get(Full(path))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new DateTimeOffset(1601, 1, 1, 0, 0, 0, TimeSpan.Zero).ToLocalTime(); }
        }

        private static long Size(string folder)
        {
            if (!Directory.Exists(folder)) return 0;
            long total = 0;
            foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch (IOException) { }
            }
            return total;
        }

        private void Guard(Action action, string message)
        {
            EnsureOpen();
            try { action(); }
            catch (IsolatedStorageException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new IsolatedStorageException(message, e); }
        }

        internal void EnsureOpen()
        {
            if (_disposed) throw new ObjectDisposedException(null, "The isolated storage file has been disposed.");
            if (_closed) throw new InvalidOperationException("The isolated storage file has been closed.");
        }
    }

    /// <summary>A file in a store: the file's own FileStream, which this wraps and forwards to, as .NET's
    /// does (the base FileStream is given the inner one's handle, not owned).</summary>
    public class IsolatedStorageFileStream : FileStream
    {
        private readonly FileStream _fs;
        private readonly IsolatedStorageFile _isf;
        private readonly bool _ownsStore;

        public IsolatedStorageFileStream(string path, FileMode mode) : this(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None, null) { }
        public IsolatedStorageFileStream(string path, FileMode mode, IsolatedStorageFile isf) : this(path, mode, mode == FileMode.Append ? FileAccess.Write : FileAccess.ReadWrite, FileShare.None, isf) { }
        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access) : this(path, mode, access, access == FileAccess.Read ? FileShare.Read : FileShare.None, 4096, null) { }
        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access, IsolatedStorageFile isf) : this(path, mode, access, access == FileAccess.Read ? FileShare.Read : FileShare.None, 4096, isf) { }
        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access, FileShare share) : this(path, mode, access, share, 4096, null) { }
        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access, FileShare share, IsolatedStorageFile isf) : this(path, mode, access, share, 4096, isf) { }
        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) : this(path, mode, access, share, bufferSize, null) { }

        public IsolatedStorageFileStream(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, IsolatedStorageFile isf)
            : this(Open(path, mode, access, share, bufferSize, isf), access, bufferSize)
        {
        }

        private IsolatedStorageFileStream((FileStream Inner, IsolatedStorageFile Store, bool Owns) opened, FileAccess access, int bufferSize)
            : base(new SafeFileHandle(opened.Inner.SafeFileHandle.DangerousGetHandle(), ownsHandle: false), access, 1)
        {
            _fs = opened.Inner;
            _isf = opened.Store;
            _ownsStore = opened.Owns;
        }

        private static (FileStream, IsolatedStorageFile, bool) Open(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, IsolatedStorageFile isf)
        {
            ArgumentNullException.ThrowIfNull(path);
            if (path.Length == 0) throw new ArgumentException("Path must not be empty.", nameof(path));
            bool owns = isf == null;
            isf ??= IsolatedStorageFile.GetUserStoreForDomain();
            string full = isf.Full(path);
            try
            {
                return (new FileStream(full, mode, access, share, bufferSize, FileOptions.None), isf, owns);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                // .NET's message names the path the application gave, not the store's folder
                throw new FileNotFoundException($"Could not find file '{path}'.", path, e);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new IsolatedStorageException("Operation not permitted.", e);
            }
        }

        public override bool CanRead => _fs.CanRead;
        public override bool CanSeek => _fs.CanSeek;
        public override bool CanWrite => _fs.CanWrite;
        public override bool IsAsync => _fs.IsAsync;
        public override long Length => _fs.Length;
        public override long Position { get => _fs.Position; set => _fs.Position = value; }

        [Obsolete("This property has been deprecated. Use IsolatedStorageFileStream's SafeFileHandle property instead.")]
        public override IntPtr Handle => throw new IsolatedStorageException("Operation not permitted.");
        public override SafeFileHandle SafeFileHandle => throw new IsolatedStorageException("Operation not permitted.");

        public override void Flush() => _fs.Flush();
        public override void Flush(bool flushToDisk) => _fs.Flush(flushToDisk);
        public override Task FlushAsync(CancellationToken cancellationToken) => _fs.FlushAsync(cancellationToken);
        public override void SetLength(long value) => _fs.SetLength(value);
        public override long Seek(long offset, SeekOrigin origin) => _fs.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) => _fs.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _fs.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _fs.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _fs.ReadAsync(buffer, cancellationToken);
        public override int ReadByte() => _fs.ReadByte();
        public override void Write(byte[] buffer, int offset, int count) => _fs.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _fs.Write(buffer);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _fs.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken) => _fs.WriteAsync(buffer, cancellationToken);
        public override void WriteByte(byte value) => _fs.WriteByte(value);
        public override IAsyncResult BeginRead(byte[] array, int offset, int numBytes, AsyncCallback userCallback, object stateObject) => _fs.BeginRead(array, offset, numBytes, userCallback, stateObject);
        public override int EndRead(IAsyncResult asyncResult) => _fs.EndRead(asyncResult);
        public override IAsyncResult BeginWrite(byte[] array, int offset, int numBytes, AsyncCallback userCallback, object stateObject) => _fs.BeginWrite(array, offset, numBytes, userCallback, stateObject);
        public override void EndWrite(IAsyncResult asyncResult) => _fs.EndWrite(asyncResult);
        public override void Lock(long position, long length) => _fs.Lock(position, length);
        public override void Unlock(long position, long length) => _fs.Unlock(position, length);

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _fs?.Dispose();
                    if (_ownsStore) _isf?.Dispose();
                }
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        public override async ValueTask DisposeAsync()
        {
            if (_fs != null) await _fs.DisposeAsync().ConfigureAwait(false);
            if (_ownsStore) _isf?.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
