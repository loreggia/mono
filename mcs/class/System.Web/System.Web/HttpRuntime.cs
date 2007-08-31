//
// System.Web.HttpRuntime.cs 
// 
// Author:
//	Miguel de Icaza (miguel@novell.com)
//
//
// Copyright (C) 2005 Novell, Inc (http://www.novell.com)
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//

//
// TODO: Call HttpRequest.CloseInputStream when we finish a request, as we are using the IntPtr stream.
//
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections;
using System.Reflection;
using System.Security;
using System.Security.Permissions;
using System.Web.Caching;
using System.Web.Configuration;
using System.Web.UI;
using System.Web.Util;
using System.Threading;

#if NET_2_0 && !TARGET_JVM
using System.CodeDom.Compiler;
using System.Web.Compilation;
#endif

namespace System.Web {
	
	// CAS - no InheritanceDemand here as the class is sealed
	[AspNetHostingPermission (SecurityAction.LinkDemand, Level = AspNetHostingPermissionLevel.Minimal)]
	public sealed class HttpRuntime {
#if TARGET_J2EE
		static QueueManager queue_manager { get { return _runtime._queue_manager; } }
		static TraceManager trace_manager { get { return _runtime._trace_manager; } }
		static Cache cache { get { return _runtime._cache; } }
		static Cache internalCache { get { return _runtime._internalCache; } }
		static WaitCallback do_RealProcessRequest;
		
		QueueManager _queue_manager;
		TraceManager _trace_manager;
		Cache _cache;
		Cache _internalCache;

		static HttpRuntime ()
		{
			do_RealProcessRequest = new WaitCallback (RealProcessRequest);
		}

		public HttpRuntime ()
		{
			WebConfigurationManager.Init ();
			_queue_manager = new QueueManager ();
			_trace_manager = new TraceManager ();
			_cache = new Cache ();
			_internalCache = new Cache();
		}

		static private HttpRuntime _runtimeInstance {
			get {
				HttpRuntime runtime = (HttpRuntime) AppDomain.CurrentDomain.GetData ("HttpRuntime");
				if (runtime == null)
					lock (typeof (HttpRuntime)) {
						runtime = (HttpRuntime) AppDomain.CurrentDomain.GetData ("HttpRuntime");
						if (runtime == null) {
							runtime = new HttpRuntime ();
							AppDomain.CurrentDomain.SetData ("HttpRuntime", runtime);
						}
					}
				return runtime;
			}
		}
		static private HttpRuntime _runtime
		{
			get
			{
				if (HttpContext.Current != null)
					return HttpContext.Current.HttpRuntimeInstance;
				else
					return _runtimeInstance;
			}
		}
#else
		static QueueManager queue_manager;
		static TraceManager trace_manager;
		static TimeoutManager timeout_manager;
		static Cache cache;
		static Cache internalCache;
		static WaitCallback do_RealProcessRequest;
		static Exception initialException;
		static bool firstRun;
		
#if NET_2_0
		static bool assemblyMappingEnabled;
		static object assemblyMappingLock = new object ();
		static object appOfflineLock = new object ();
#endif
		
		static HttpRuntime ()
		{
			firstRun = true;
#if NET_2_0
			try {
				WebConfigurationManager.Init ();
			} catch (Exception ex) {
				initialException = ex;
			}
			
#endif

			// The classes in whose constructors exceptions may be thrown, should be handled the same way QueueManager
			// and TraceManager are below. The constructors themselves MUST NOT throw any exceptions - we MUST be sure
			// the objects are created here. The exceptions will be dealt with below, in RealProcessRequest.
			queue_manager = new QueueManager ();
			if (queue_manager.HasException)
				initialException = queue_manager.InitialException;

			trace_manager = new TraceManager ();
			if (trace_manager.HasException)
					initialException = trace_manager.InitialException;

			timeout_manager = new TimeoutManager ();
			cache = new Cache ();
			internalCache = new Cache ();
			do_RealProcessRequest = new WaitCallback (RealProcessRequest);
		}

#if ONLY_1_1
		[SecurityPermission (SecurityAction.Demand, UnmanagedCode = true)]
#endif
		public HttpRuntime ()
		{

		}
#endif
		
#region AppDomain handling
		//
		// http://radio.weblogs.com/0105476/stories/2002/07/12/executingAspxPagesWithoutAWebServer.html
		//
		public static string AppDomainAppId {
			[AspNetHostingPermission (SecurityAction.Demand, Level = AspNetHostingPermissionLevel.High)]
			get {
				//
				// This value should not change across invocations
				//
				string dirname = (string) AppDomain.CurrentDomain.GetData (".appId");
				if ((dirname != null) && (dirname.Length > 0) && SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		// Physical directory for the application
		public static string AppDomainAppPath {
			get {
				string dirname = (string) AppDomain.CurrentDomain.GetData (".appPath");
				if (SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		public static string AppDomainAppVirtualPath {
			get {
				return (string) AppDomain.CurrentDomain.GetData (".appVPath");
			}
		}

		public static string AppDomainId {
			[AspNetHostingPermission (SecurityAction.Demand, Level = AspNetHostingPermissionLevel.High)]
			get {
				return (string) AppDomain.CurrentDomain.GetData (".domainId");
			}
		}

		public static string AspInstallDirectory {
			get {
				string dirname = (string) AppDomain.CurrentDomain.GetData (".hostingInstallDir");
				if ((dirname != null) && (dirname.Length > 0) && SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}
#endregion
		
		public static string BinDirectory {
			get {
				string dirname = AppDomain.CurrentDomain.SetupInformation.PrivateBinPath;
				if (SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		public static Cache Cache {
			get {
				return cache;
			}
		}

		internal static Cache InternalCache {
			get {
				return internalCache;
			}
		}
		
		public static string ClrInstallDirectory {
			get {
				string dirname = Path.GetDirectoryName (typeof (Object).Assembly.Location);
				if ((dirname != null) && (dirname.Length > 0) && SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		public static string CodegenDir {
			get {
				string dirname = AppDomain.CurrentDomain.SetupInformation.DynamicBase;
				if (SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		[MonoTODO]
		public static bool IsOnUNCShare {
			[AspNetHostingPermission (SecurityAction.Demand, Level = AspNetHostingPermissionLevel.Low)]
			get {
				throw new NotImplementedException ();
			}
		}

		public static string MachineConfigurationDirectory {
			get {
				string dirname = Path.GetDirectoryName (ICalls.GetMachineConfigPath ());
				if ((dirname != null) && (dirname.Length > 0) && SecurityManager.SecurityEnabled) {
					new FileIOPermission (FileIOPermissionAccess.PathDiscovery, dirname).Demand ();
				}
				return dirname;
			}
		}

		
		[SecurityPermission (SecurityAction.Demand, UnmanagedCode = true)]
		public static void Close ()
		{
			// Remove all items from cache.
		}

		static void QueuePendingRequests ()
		{
			HttpWorkerRequest request = queue_manager.GetNextRequest (null);
			if (request == null)
				return;
			ThreadPool.QueueUserWorkItem (do_RealProcessRequest, request);
		}

#if NET_2_0
		static readonly string[] app_offline_files = {"app_offline.htm", "App_Offline.htm", "APP_OFFLINE.HTM"};
		static FileSystemWatcher app_offline_watcher;

		static void AppOfflineFileRenamed (object sender, RenamedEventArgs args)
		{
			AppOfflineFileChanged (sender, args);
		}
		
		static void AppOfflineFileChanged (object sender, FileSystemEventArgs args)
	        {
	        	lock (appOfflineLock) {
				HttpApplicationFactory.DisableWatchers ();
				app_offline_watcher.EnableRaisingEvents = false;
				
				// Restart application
				HttpRuntime.UnloadAppDomain();
			}
	        }
		
		static bool AppIsOffline (HttpContext context)
		{
			string app_path = context.Request.PhysicalApplicationPath;
			string aof_path = null;
			bool haveOfflineFile = false;

			foreach (string aof in app_offline_files) {
				aof_path = Path.Combine (app_path, aof);
				if (File.Exists (aof_path)) {
					haveOfflineFile = true;
					break;
				}
			}

			lock (appOfflineLock) {
				if (!haveOfflineFile) {
					if (HttpApplicationFactory.ApplicationDisabled) {
						HttpApplicationFactory.ApplicationDisabled = false;
						HttpApplicationFactory.EnableWatchers ();
					}
					
					return false;
				}

				HttpApplicationFactory.ApplicationDisabled = true;
				HttpApplicationFactory.DisableWatchers ();

				FileSystemEventHandler seh = new FileSystemEventHandler (AppOfflineFileChanged);
				RenamedEventHandler reh = new RenamedEventHandler (AppOfflineFileRenamed);
				
				app_offline_watcher = new FileSystemWatcher ();
				app_offline_watcher.Path = Path.GetDirectoryName (aof_path);
				app_offline_watcher.Filter = Path.GetFileName (aof_path);
				app_offline_watcher.NotifyFilter |= NotifyFilters.Size;
				app_offline_watcher.Deleted += seh;
				app_offline_watcher.Changed += seh;
				app_offline_watcher.Renamed += reh;
				app_offline_watcher.EnableRaisingEvents = true;
			}

			HttpResponse response = context.Response;
			response.Clear ();
			response.ContentType = "text/html";
			response.ExpiresAbsolute = DateTime.UtcNow;
			response.TransmitFile (aof_path, true);
			
			context.Request.ReleaseResources ();
			context.Response.ReleaseResources ();
			HttpContext.Current = null;
			
			return true;
		}
#endif
		
		static void RealProcessRequest (object o)
		{
			HttpContext context = new HttpContext ((HttpWorkerRequest) o);
			HttpContext.Current = context;

			bool error = false;
#if !TARGET_J2EE
			if (firstRun) {
				firstRun = false;
				if (initialException != null) {
					FinishWithException ((HttpWorkerRequest) o, new HttpException ("Initial exception", initialException));
					error = true;
				}
			}
#endif

#if NET_2_0
			if (AppIsOffline (context))
				return;
#endif
			
			//
			// Get application instance (create or reuse an instance of the correct class)
			//
			HttpApplication app = null;
			if (!error) {
				try {
					app = HttpApplicationFactory.GetApplication (context);
				} catch (Exception e) {
					FinishWithException ((HttpWorkerRequest) o, new HttpException ("", e));
					error = true;
				}
			}
			
			if (error) {
				context.Request.ReleaseResources ();
				context.Response.ReleaseResources ();
				HttpContext.Current = null;
			} else {
				context.ApplicationInstance = app;
				
				//
				// Ask application to service the request
				//
				IHttpAsyncHandler ihah = app;

				IAsyncResult appiar = ihah.BeginProcessRequest (context, new AsyncCallback (request_processed), context);
				ihah.EndProcessRequest (appiar);

				HttpApplicationFactory.Recycle (app);
			}
			
			QueuePendingRequests ();
		}
		
		//
		// ProcessRequest method is executed in the AppDomain of the application
		//
		// Observations:
		//    ProcessRequest does not guarantee that `wr' will be processed synchronously,
		//    the request can be queued and processed later.
		//
		[AspNetHostingPermission (SecurityAction.Demand, Level = AspNetHostingPermissionLevel.Medium)]
		public static void ProcessRequest (HttpWorkerRequest wr)
		{
			if (wr == null)
				throw new ArgumentNullException ("wr");
			//
			// Queue our request, fetch the next available one from the queue
			//
			HttpWorkerRequest request = queue_manager.GetNextRequest (wr);
			if (request == null)
				return;

			RealProcessRequest (request);
		}

		//
		// Callback to be invoked by IHttpAsyncHandler.BeginProcessRequest
		//
		static void request_processed (IAsyncResult iar)
		{
			HttpContext context = (HttpContext) iar.AsyncState;

			context.Request.ReleaseResources ();
			context.Response.ReleaseResources ();
		}

		//
		// Called when we are shutting down or we need to reload an application
		// that has been modified (touch global.asax) 
		//
		[SecurityPermission (SecurityAction.Demand, UnmanagedCode = true)]
		public static void UnloadAppDomain ()
		{
			//
			// TODO: call ReleaseResources
			//
			ThreadPool.QueueUserWorkItem (new WaitCallback (ShutdownAppDomain), null);
		}

		//
		// Shuts down the AppDomain
		//
		static void ShutdownAppDomain (object args)
		{
			queue_manager.Dispose ();
			// This will call Session_End if needed.
			InternalCache.InvokePrivateCallbacks ();
			// Kill our application.
			HttpApplicationFactory.Dispose ();
			ThreadPool.QueueUserWorkItem (new WaitCallback (DoUnload), null);
		}

		static void DoUnload (object state)
		{
			if (Environment.GetEnvironmentVariable ("MONO_ASPNET_NODELETE") == null)
				System.Web.Hosting.ApplicationHost.ClearDynamicBaseDirectory (
					AppDomain.CurrentDomain.SetupInformation.DynamicBase
				);
#if TARGET_J2EE
			// No unload support for appdomains under Grasshopper
#else
			AppDomain.Unload (AppDomain.CurrentDomain);
#endif
		}

                static string content503 = "<!DOCTYPE HTML PUBLIC \"-//IETF//DTD HTML 2.0//EN\">\n" +
			"<html><head>\n<title>503 Server Unavailable</title>\n</head><body>\n" +
			"<h1>Server Unavailable</h1>\n" +
			"</body></html>\n";

		static void FinishWithException (HttpWorkerRequest wr, HttpException e)
		{
			int code = e.GetHttpCode ();
			wr.SendStatus (code, HttpWorkerRequest.GetStatusDescription (code));
			wr.SendUnknownResponseHeader ("Connection", "close");
			wr.SendUnknownResponseHeader ("Date", DateTime.Now.ToUniversalTime ().ToString ("r"));
			Encoding enc = Encoding.ASCII;
			wr.SendUnknownResponseHeader ("Content-Type", "text/html; charset=" + enc.WebName);
			string msg = e.GetHtmlErrorMessage ();
			byte [] contentBytes = enc.GetBytes (msg);
			wr.SendUnknownResponseHeader ("Content-Length", contentBytes.Length.ToString ());
			wr.SendResponseFromMemory (contentBytes, contentBytes.Length);
			wr.FlushResponse (true);
			wr.CloseConnection ();
		}

		//
		// This is called from the QueueManager if a request
		// can not be processed (load, no resources, or
		// appdomain unload).
		//
		static internal void FinishUnavailable (HttpWorkerRequest wr)
		{
			wr.SendStatus (503, "Service unavailable");
			wr.SendUnknownResponseHeader ("Connection", "close");
			wr.SendUnknownResponseHeader ("Date", DateTime.Now.ToUniversalTime ().ToString ("r"));
			Encoding enc = Encoding.ASCII;
			wr.SendUnknownResponseHeader ("Content-Type", "text/html; charset=" + enc.WebName);
			byte [] contentBytes = enc.GetBytes (content503);
			wr.SendUnknownResponseHeader ("Content-Length", contentBytes.Length.ToString ());
			wr.SendResponseFromMemory (contentBytes, contentBytes.Length);
			wr.FlushResponse (true);
			wr.CloseConnection ();
		}

#if NET_2_0 && !TARGET_J2EE
		static internal void WritePreservationFile (Assembly asm, string genericNameBase)
		{
			if (asm == null)
				throw new ArgumentNullException ("asm");
			if (String.IsNullOrEmpty (genericNameBase))
				throw new ArgumentNullException ("genericNameBase");

			string compiled = Path.Combine (AppDomain.CurrentDomain.SetupInformation.DynamicBase,
							genericNameBase + ".compiled");
			PreservationFile pf = new PreservationFile ();
			try {
				pf.VirtualPath = String.Format ("/{0}/", genericNameBase);

				AssemblyName an = asm.GetName ();
				pf.Assembly = an.Name;
				pf.ResultType = BuildResultTypeCode.TopLevelAssembly;
				pf.Save (compiled);
			} catch (Exception ex) {
				throw new HttpException (
					String.Format ("Failed to write preservation file {0}", genericNameBase + ".compiled"),
					ex);
			}
		}
		
		static Assembly ResolveAssemblyHandler(object sender, ResolveEventArgs e)
		{
			AssemblyName an = new AssemblyName (e.Name);
			string dynamic_base = AppDomain.CurrentDomain.SetupInformation.DynamicBase;
			string compiled = Path.Combine (dynamic_base, an.Name + ".compiled");

			if (!File.Exists (compiled))
				return null;

			PreservationFile pf;
			try {
				pf = new PreservationFile (compiled);
			} catch (Exception ex) {
				throw new HttpException (
					String.Format ("Failed to read preservation file {0}", an.Name + ".compiled"),
					ex);
			}
			
			Assembly ret = null;
			try {
				string asmPath = Path.Combine (dynamic_base, pf.Assembly + ".dll");
				ret = Assembly.LoadFrom (asmPath);
			} catch (Exception) {
				// ignore
			}
			
			return ret;
		}
		
		internal static void EnableAssemblyMapping (bool enable)
		{
			lock (assemblyMappingLock) {
				if (assemblyMappingEnabled == enable)
					return;
				if (enable)
					AppDomain.CurrentDomain.AssemblyResolve += new ResolveEventHandler (ResolveAssemblyHandler);
				else
					AppDomain.CurrentDomain.AssemblyResolve -= new ResolveEventHandler (ResolveAssemblyHandler);
			}
		}
#endif
		
		internal static TraceManager TraceManager {
			get {
				return trace_manager;
			}
		}

#if !TARGET_JVM
		internal static TimeoutManager TimeoutManager {
			get {
				return timeout_manager;
			}
		}
#endif
	}
}
