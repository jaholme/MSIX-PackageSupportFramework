﻿//-------------------------------------------------------------------------------------------------------
// Copyright (C) TMurgent Technologies. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
//-------------------------------------------------------------------------------------------------------
//
// NOTE: PsfMonitor is a "procmon"-like display of events captured via the PSF TraceShim.

using Microsoft.Diagnostics.Tracing;  // consumer
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session; // controller
using System;
using System.ComponentModel;  // backgroundworker
using System.Threading;
using System.Windows;
using System.Collections.Generic;
using System.Collections.ObjectModel;  // ObservableCollection

namespace PsfMonitor
{
    public partial class MainWindow : Window
    {
        public TraceEventSession TraceEventSession_ProcsKernel = null;
        public bool rememberToDisableSession_ProcsKernel = false;
        public bool PleaseStopCollecting = false;
        public int FilterOnProcessId = -1;

        public List<EventItem> _TKernelEventListItems = new List<EventItem>();
        public Object _TKernelEventListsLock = new Object();

        Dictionary<UInt64, string> _KCBs = new Dictionary<UInt64, string>();
        Dictionary<UInt64, string> _TKCBs = new Dictionary<UInt64, string>();
        public Object _TKCBsListLock = new object();

        private int MAX_KCBS = 100000;

        private void KernelTraceInBackground_Start()
        {
            if (kerneleventbgw == null)
                kerneleventbgw = new BackgroundWorker();
            else if (kerneleventbgw.IsBusy)
                return;
            else
                kerneleventbgw = new BackgroundWorker();



            // Do processing in the background
            kerneleventbgw.WorkerSupportsCancellation = true;
            kerneleventbgw.WorkerReportsProgress = true;
            kerneleventbgw.DoWork += KernelTrace_DoWork;
            kerneleventbgw.ProgressChanged += KernelTrace_ProgressChanged;
            kerneleventbgw.RunWorkerCompleted += KernelTrace_RunWorkerCompleted;
            kerneleventbgw.RunWorkerAsync();
        } // ETWTraceInBackground_Start()

        private void KernelTrace_DoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker worker = sender as BackgroundWorker;
            Thread.CurrentThread.Name = "ETWReader";

            EnableKernelTrace(worker);
        }

        private void KernelTrace_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            try
            {
                lock (_TKernelEventListsLock) //_TKCBsListLock)
                {
                    if (_TKCBs.Count > 0)
                    {
                        foreach (UInt64 k in _TKCBs.Keys)
                        {
                            // avoid catches
                            string s = null;
                            _TKCBs.TryGetValue(k, out s);
                            if (s != null)
                            {
                                try
                                {
                                    if (_KCBs.Count < MAX_KCBS)  // temp
                                    {
                                        string olds = null;
                                        _KCBs.TryGetValue(k, out olds);
                                        if (olds == null)
                                        {
                                            _KCBs.Add(k, s);
                                            ApplyKCBtoPastRegistryEvents(k, s);
                                        }
                                    }
                                }
                                catch {  /* event thrown if key exists, which should not happen here */ }
                            }
                        }
                        _TKCBs.Clear();
                    }
                }
                lock (_TKernelEventListsLock) //_TKCBsListLock)
                {
                    if (_TKernelEventListItems.Count > 0)
                    {
                        foreach (EventItem ei in _TKernelEventListItems)
                        {
                            AppplyFilterToEventItem(ei);
                            if (IsPaused)
                                ei.IsPauseHidden = true;
                            ApplyPastKCBsToRegistryEvent(ei);
                            _ModelEventItems.Add(ei);
                        }
                        _TKernelEventListItems.Clear();

                        UpdateFilteredViewList();
                    }
                    else
                        EventsGrid.Items.Refresh();
                }
            }
            catch
            {
                ;
            }
        }

        private void KernelTrace_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            Status.Text = "NonKernel";
        }

        private void ApplyKCBtoPastRegistryEvents(UInt64 k, string s)
        {
            foreach (EventItem ei in _ModelEventItems)
            {
                if (ei.EventSource.StartsWith("Registry/"))
                {
                    if (ei.Inputs.Contains("KeyName=\n") &&
                        !ei.Inputs.Contains(")\nKeyName="))
                    {
                        if (ei.Inputs.Contains("Key=      \t" + k.ToString() + "\n"))
                        {
                            ei.Inputs.Replace("\nKeyName=", " (" + s + ")\nKeyName=");
                        }
                    }
                }
            }
        }
        private void ApplyPastKCBsToRegistryEvent(EventItem ei)
        {
            if (ei.Event.StartsWith("Registry/"))
            {
                if (ei.Inputs.Contains("KeyName=\n") &&
                    !ei.Inputs.Contains(")\nKeyName="))
                {
                    string matchh = ei.Inputs.Substring(10, ei.Inputs.IndexOf('\n') - 8);
                    foreach (UInt64 key in _KCBs.Keys)
                    {
                        if (key.ToString().Equals(matchh))
                        {
                            try
                            {
                                ei.Inputs.Replace("\nKeyName=", " (" + _KCBs[key] + ")\nKeyName=");
                            }
                            catch { }
                            break;
                        }
                    }
                }
            }
        }

        int TSM_ProcID;
        void EnableKernelTrace(BackgroundWorker worker)
        {
            TSM_ProcID = System.Diagnostics.Process.GetCurrentProcess().Id;
            using (TraceEventSession_ProcsKernel = new TraceEventSession(KernelTraceEventParser.KernelSessionName))
            {
                bool restarted = false;
                TraceEventSession_ProcsKernel.StopOnDispose = true;
                try
                {
                    restarted = TraceEventSession_ProcsKernel.EnableKernelProvider(   KernelTraceEventParser.Keywords.FileIOInit
                                                                                      | KernelTraceEventParser.Keywords.FileIO
                                                                                      | KernelTraceEventParser.Keywords.Registry
                                                                                      | KernelTraceEventParser.Keywords.ImageLoad
                                                                                      | KernelTraceEventParser.Keywords.Process
                                                                                      
                                                                                      | KernelTraceEventParser.Keywords.DiskFileIO
                                                                                      | KernelTraceEventParser.Keywords.DiskIOInit
                                                                                      | KernelTraceEventParser.Keywords.DiskIO
                                                                                      // | KernelTraceEventParser.Keywords.NetworkTCPIP
                                                                                      // | KernelTraceEventParser.Keywords.SystemCall
                                                                                      // | KernelTraceEventParser.Keywords.Driver
                                                                                        );
                }
                catch (Exception ex)
                {
                    ;
                }
                if (!restarted)
                {
                    rememberToDisableSession_ProcsKernel = true;
                }
                try
                {
                    TraceEventSession_ProcsKernel.Source.Kernel.All +=
                         delegate (TraceEvent data)
                         {
                             if (!PleaseStopCollecting &&
                                 data.ProcessID != TSM_ProcID)
                             {
                                 int pid = (int)data.ProcessID;
                                 if (ProcIDsOfTarget.Count == 0 || IsPidInProdIDsList(pid))
                                 {

                                     if (!data.EventName.StartsWith("Thread/") &&
                                         !data.EventName.StartsWith("Image/DC") &&
                                         !data.EventName.StartsWith("Process/DC"))  // disposes of most of the chaff
                                     {

                                         if (data.EventName.StartsWith("Process/Start"))
                                         {
                                             // (int)ProcessID, (int)ParentID, ImageFileName, (unknown)PageDirectoryBase, (Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags)Flags, (int)SessionID, (Int)ExitStatus, (ulong)UniqueProcessKey, CommandLine, PackageFullName, (string)ApplicationID
                                             // ExitStatus would not be valid
                                             try
                                             {
                                                 string inputs = "ImageFileName=\t" + data.PayloadStringByName("ImageFileName");
                                                 inputs += "\nSessionID=\t" + data.PayloadStringByName("SessionID");
                                                 inputs += "\nFlags=    \t" + Interpret_KernelProcessFlags((Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags)data.PayloadByName("Flags"));
                                                 if (((Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags)data.PayloadByName("Flags") & Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags.PackageFullName) != 0)
                                                     inputs += "\nPackageFullName=\t" + data.PayloadStringByName("PackageFullName").ToString();
                                                 string appid = data.PayloadStringByName("ApplicationID");
                                                 if (appid != null && appid.Length > 0)
                                                     inputs += "\nApplicationID=\t" + appid;
                                                 inputs += "\nParentID=\t" + data.PayloadStringByName("ParentID");
                                                 inputs += "\nCommandLine\t" + data.PayloadStringByName("CommandLine");

                                                 string outputs = "ProcessID=\t" + data.PayloadStringByName("ProcessID");
                                                 outputs += "\nUniqueProcessKey=\t" + data.PayloadStringByName("UniqueProcessKey");

                                                 EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                 lock (_TKernelEventListsLock)
                                                 {
                                                     _TKernelEventListItems.Add(ei);
                                                 }
                                                 worker.ReportProgress((int)data.EventIndex);
                                             }
                                             catch { }
                                         }
                                         else if (data.EventName.StartsWith("Process/Stop"))
                                         {
                                             // (int)ProcessID, (int)ParentID, ImageFileName, (unknown)PageDirectoryBase, (Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags)Flags, (int)SessionID, (Int)ExitStatus, (ulong)UniqueProcessKey, CommandLine, PackageFullName, (string)ApplicationID
                                             // SessionID is always 0
                                             try
                                             {
                                                 string inputs = "ImageFileName=\t" + data.PayloadStringByName("ImageFileName");
                                                 inputs += "\nSessionID=\t" + data.PayloadStringByName("SessionID");
                                                 inputs += "\nUniqueProcessKey=\t" + data.PayloadStringByName("UniqueProcessKey");
                                                 inputs += "\nCommandLine\t" + data.PayloadStringByName("CommandLine");

                                                 string outputs = "ExitStatus=\t" + data.PayloadStringByName("ExitStatus");

                                                 EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                 lock (_TKernelEventListsLock)
                                                 {
                                                     _TKernelEventListItems.Add(ei);
                                                 }
                                                 worker.ReportProgress((int)data.EventIndex);
                                             }
                                             catch (Exception ex)
                                             {
                                                 ;
                                             }
                                         }
                                         else if (data.EventName.StartsWith("Image/Load"))
                                         {
                                             try
                                             {
                                                 // (ulong)ImageBase, (int)ImageSize, (int)ImageChecksum, (System.DateTime)TimeDateStamp, (ulong)DefaultBase, (System.DateTime)BuildTime, FileName
                                                 string inputs = "FileName=  \t" + data.PayloadStringByName("FileName");
                                                 string outputs = "";

                                                 EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                 lock (_TKernelEventListsLock)
                                                 {
                                                     _TKernelEventListItems.Add(ei);
                                                 }
                                                 worker.ReportProgress((int)data.EventIndex);


                                             }
                                             catch
                                             {
                                                 ;
                                             }
                                         }
                                         else if (data.EventName.StartsWith("Image/Unload"))
                                         {
                                             ; // ignore
                                         }
                                         else if (data.EventName.StartsWith("FileIOInit"))
                                         {
                                             ;  // ignore for now
                                         }
                                         else if (data.EventName.StartsWith("FileIO"))
                                         {
                                             if (data.EventName.StartsWith("FileIO/Query")) // also catch QueryInfo
                                             {
                                                 // FileIO/Query     (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (ulong)ExtraInfo, InfoClass, FileName
                                                 // FileIO/QueryInfo (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (ulong)ExtraInfo, InfoClass, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName=\t" + data.PayloadStringByName("FileName");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nExtraInfo=  \t0x" + ((ulong)data.PayloadByName("ExtraInfo")).ToString("x");
                                                         string outputs = "FileObject=\t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         outputs += "\nIrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Create"))
                                             {
                                                 // Microsoft.Diagnostics.Tracing.Parsers.Kernel.[CreateDisposition,CreateOptions]
                                                 ; // IntPtr, (ulong)FileObject, CreateOptions, CreateDisposition, (System.IO.FileAttributes)FileAttributes, (System.IO.FileShare)ShareAccess, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName=\t" + data.PayloadStringByName("FileName");
                                                         inputs += "\nCreateOptions=\t";
                                                         inputs += Interpret_KernelFileCreateOptions((Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions)data.PayloadByName("CreateOptions"));
                                                         inputs += "\nCreateDisposition=";
                                                         inputs += Interpret_KernelFileCreateDispositions((Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition)data.PayloadByName("CreateDispostion")); // Yes, Microsoft misspelled this one!
                                                         //inputs += " (" + data.PayloadStringByName("CreateDispostion") + ")";  // Yes, Microsoft misspelled this one!
                                                         inputs += "\nFileAttributes=\t";// + ((UInt32)data.PayloadByName("FileAttributes")).ToString("x");
                                                         inputs += " (" + data.PayloadStringByName("FileAttributes") + ")";
                                                         inputs += "\nShareAccess=\t"; //+ ((UInt32)data.PayloadByName("ShareAccess")).ToString("x");
                                                         inputs += " (" + data.PayloadStringByName("ShareAccess") + ")";

                                                         string outputs = "FileObject=\t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         outputs += "\nIrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "" );
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/FileCreate"))
                                             {
                                                 // FileIO/Close (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName=   \t" + data.PayloadStringByName("FileName");
                                                         string outputs = "FileKey=   \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Read"))
                                             {
                                                 // FileIO/Read Offset, (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (int)IoFlags, (int)IoSize, (long)IoOffset, IoFlags, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "IrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nIoFlags=    \t0x" + ((int)data.PayloadByName("IoFlags")).ToString("x");
                                                         inputs += "\nOffset=     \t0x" + ((long)data.PayloadByName("Offset")).ToString("x");
                                                         inputs += "\nIoSize=     \t0x" + ((int)data.PayloadByName("IoSize")).ToString("x");

                                                         string outputs = "FileObject=  \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");


                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Write"))
                                             {
                                                 // FileIO/Write Offset, (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (int)IoSize, (long)IoOffset, (int)IoFlags, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName=\t" + data.PayloadStringByName("FileName");
                                                         inputs += "\nIrpPtr=     \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject  \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nIoFlags=    \t0x" + ((int)data.PayloadByName("IoFlags")).ToString("x");
                                                         inputs += "\nOffset=     \t0x" + ((long)data.PayloadByName("Offset")).ToString("x");
                                                         inputs += "\nIoSize=     \t0x" + ((int)data.PayloadByName("IoSize")).ToString("x");
                                                         string outputs = "";

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Close"))
                                             {
                                                 // FileIO/Close (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName=\t" + data.PayloadStringByName("FileName");
                                                         inputs += "\nIrpPtr=     \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject= \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         string outputs = "";

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Cleanup"))
                                             {
                                                 // FileIO/Cleanup (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "FileName= \t" + data.PayloadStringByName("FileName");
                                                         inputs += "\nIrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject=\t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=   \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");

                                                         string outputs = "";
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/OperationEnd"))
                                             {
                                                 ;//FileIO/OperationEnd: (ulong)IrpPtr, (ulong)ExtraInfo, (int)NtStatus)))
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "IrpPtr=      \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");

                                                         string outputs = "NtStatus= \t0x" + ((int)data.PayloadByName("NtStatus")).ToString("x");
                                                         outputs += "\nExtraInfo=    \t0x" + ((ulong)data.PayloadByName("ExtraInfo")).ToString("x");
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/DirEnum"))
                                             {
                                                 // FileIO/DirEnum: (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (string)DirectoryName, (int)Length, (int)InfoClass, (int)FileIndex, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "DirectoryName=" + data.PayloadStringByName("DirectoryName");
                                                         inputs += "\nIrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject= \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");

                                                         string outputs = "FileName=\t" + data.PayloadStringByName("FileName");
                                                         outputs += "\nFileIndex= \t0x" + ((int)data.PayloadByName("FileIndex")).ToString("x");
                                                         outputs += "\nLength=    \t0x" + ((int)data.PayloadByName("Length")).ToString("x");
                                                         outputs += "\nInfoClass= \t0x" + ((int)data.PayloadByName("InfoClass")).ToString("x");

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/SetInfo"))
                                             {
                                                 // FileIO/SetInfo:   (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (ulong)ExtraInfo, (int)InfoClass, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "IrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject= \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nExtraInfo=  \t0x" + ((ulong)data.PayloadByName("ExtraInfo")).ToString("x");
                                                         inputs += "\nInfoClass=  \t0x" + ((int)data.PayloadByName("InfoClass")).ToString("x");
                                                         inputs += "\nFileName=   \t" + data.PayloadStringByName("FileName");

                                                         string outputs = "";
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Rename"))
                                             {
                                                 // FileIO/Rename:    (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (ulong)ExtraInfo, (int)InfoClass, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "IrpPtr=   \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "\nFileObject= \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nExtraInfo=  \t0x" + ((ulong)data.PayloadByName("ExtraInfo")).ToString("x");
                                                         inputs += "\nInfoClass=  \t0x" + ((int)data.PayloadByName("InfoClass")).ToString("x");
                                                         inputs += "\nFileName=   \t" + data.PayloadStringByName("FileName");

                                                         string outputs = ""; 
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Delete"))
                                             {
                                                 // FileIO/Delete:    (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "FileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nFileName=   \t" + data.PayloadStringByName("FileName");

                                                         string outputs = "";
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/FileDelete"))
                                             {
                                                 // FileIO/FileDelete:    (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "FileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nFileName=   \t" + data.PayloadStringByName("FileName");

                                                         string outputs = "";
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/Flush"))
                                             {
                                                 // FileIO/Flush:    (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {

                                                         string inputs = "IrpPtr=    \t0x" + ((ulong)data.PayloadByName("IrpPtr")).ToString("x");
                                                         inputs += "FileObject= \t0x" + ((ulong)data.PayloadByName("FileObject")).ToString("x");
                                                         inputs += "FileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");
                                                         inputs += "\nFileName= \t" + data.PayloadStringByName("FileName");

                                                         string outputs = "";
                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("FileIO/DirNotify"))
                                             {
                                                 // FileIO/DirNotify: (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, DirectoryName, Length, InfoClass, FileIndex, FileName 
                                             }
                                             else if (data.EventName.StartsWith("FileIO/FSControl"))
                                             {
                                                 // FileIO/FSControl: (ulong)IrpPtr, (ulong)FileObject, (ulong)FileKey, (ulong)ExtraInfo, (int)InfoClass, FileName
                                             }
                                             else
                                             {
                                                 ;
                                             }
                                         }
                                         else if (data.EventName.StartsWith("File/"))
                                         {
                                             ;
                                         }
                                         else if (data.EventName.StartsWith("Registry/"))
                                         {
                                             if (data.EventName.StartsWith("Registry/KCBDelete") ||
                                                 data.EventName.StartsWith("Registry/KCBRundownEnd") ||
                                                 data.EventName.StartsWith("Registry/KCBCreate")
                                                 )
                                             {
                                                 UInt64 k = (UInt64)data.PayloadByName("KeyHandle");
                                                 string n = data.PayloadStringByName("KeyName");
                                                 bool added = false;
                                                 lock (_TKernelEventListsLock) //_TKCBsListLock)
                                                 {
                                                     try
                                                     {
                                                         string olds = null;
                                                         _TKCBs.TryGetValue(k, out olds);
                                                         if (olds == null)
                                                         {
                                                             _TKCBs.Add(k, n);
                                                             added = true;
                                                         }
                                                     }
                                                     catch {  /* exception if key exists */ }
                                                 }
                                                 if (added)
                                                     worker.ReportProgress((int)0);
                                             }
                                             //else if (data.EventName.StartsWith("Registry/KCBCreate"))
                                             //{
                                             //    ;
                                             //}
                                             else if (data.EventName.StartsWith("Registry/EnumerateValueKey"))
                                             {
                                                 // Registry/EnumerateValueKey: (int)Status, (ulong)KeyHandle, (double)ElapsedTimeMSec, (string)KeyName, (string)ValueName, (int)Index
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "KeyHandle=\t0x" + ((ulong)data.PayloadByName("KeyHandle")).ToString("x");
                                                         inputs += "\nKeyName=  \t" + data.PayloadStringByName("KeyName");
                                                         inputs += "\nIndex=    \t0x" + ((int)data.PayloadByName("Index")).ToString("x");

                                                         string outputs = "Status= \t" + data.PayloadStringByName("Status");
                                                         outputs += "\nValueName=\t" + data.PayloadStringByName("ValueName");
                                                         outputs += "\nElapsedTimeMS=\t" + ((double)data.PayloadByName("ElapsedTimeMSec")).ToString();

                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else  // other registry
                                             {
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     string inputs = "Key=      \t" + data.PayloadStringByName("KeyHandle") +
                                                                   "\nKeyName= \t" + data.PayloadStringByName("KeyName") +
                                                                   "\nValueName=\t" + data.PayloadStringByName("ValueName");
                                                     string outputs = "Status=" + data.PayloadStringByName("Status");

                                                     EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                     lock (_TKernelEventListsLock)
                                                     {
                                                         _TKernelEventListItems.Add(ei);
                                                     }
                                                     worker.ReportProgress((int)data.EventIndex);
                                                 }
                                             }
                                         }

                                         else if (data.EventName.StartsWith("EventTrace"))
                                         {
                                             // EventTrace/Extension
                                             // EventTrace/EndExtension
                                             // EventTrace/RundownComplete  // end of a previously running process
                                             ; // ignore
                                         }

                                         else if (data.EventName.StartsWith("DiskIOInit"))
                                         {
                                             ;
                                         }
                                         else if (data.EventName.StartsWith("DiskIO"))
                                         {
                                             if (data.EventName.StartsWith("DiskIO/WriteInit"))
                                             {
                                                 // DiskIO/WriteInit:  (ulong)Irp                                                
                                                 ;
                                             }
                                             else if (data.EventName.StartsWith("DiskIO/Write"))
                                             {
                                                 // DiskIO/Write:  (int)DiskNumber, (Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags)IrpFlags, Priority, TransferSize, ByteOffset, (ulong)Irp, (double)ElapsedTimeMSec, DiskServiceTimeMSec, (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "DiskNumber=    \t0x" + ((int)data.PayloadByName("DiskNumber")).ToString("x");
                                                         inputs += "\nIrpFlags=    \t" + Interpret_KernelFile_IrpFlags((Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags)data.PayloadByName("IrpFlags"));
                                                         inputs += "\nPriority=    \t" + Interpret_KernelFile_Priority((Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority)data.PayloadByName("Priority"));
                                                         inputs += "\nTransferSize=\t0x" + ((int)data.PayloadByName("Priority")).ToString("x");
                                                         inputs += "\nByteOffset=  \t0x" + ((long)data.PayloadByName("ByteOffset")).ToString("x");
                                                         inputs += "\nIrp=      \t0x" + ((ulong)data.PayloadByName("Irp")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");

                                                         string outputs = "ElapsedTimeMS=\t" + data.PayloadStringByName("ElapsedTimeMS");
                                                         outputs += "\nDiskServiceTimeMS=\t" + data.PayloadStringByName("DiskServiceTimeMSec");


                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("DiskIO/ReadInit"))
                                             {
                                                 // DiskIO/ReadInit: (ulong)Irp
                                                 ;
                                             }
                                             else if (data.EventName.StartsWith("DiskIO/Read"))
                                             {
                                                 // DiskIO/Read:  (int)DiskNumber, (Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags)IrpFlags, Priority, TransferSize, ByteOffset, (ulong)Irp, (double)ElapsedTimeMSec, DiskServiceTimeMSec, (ulong)FileKey, FileName
#if DEBUG
                                                 if (_ModelEventItems.Count < 10000)
#else
                                                 if (FilterOnProcessId == pid)
#endif
                                                 {
                                                     try
                                                     {
                                                         string inputs = "DiskNumber=    \t0x" + ((int)data.PayloadByName("DiskNumber")).ToString("x");
                                                         inputs += "\nIrpFlags=    \t" + Interpret_KernelFile_IrpFlags((Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags)data.PayloadByName("IrpFlags"));
                                                         inputs += "\nPriority=    \t" + Interpret_KernelFile_Priority((Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority)data.PayloadByName("Priority"));
                                                         inputs += "\nTransferSize= \t0x" + ((int)data.PayloadByName("Priority")).ToString("x");
                                                         inputs += "\nByteOffset=   \t0x" + ((long)data.PayloadByName("ByteOffset")).ToString("x");
                                                         inputs += "\nIrp=      \t0x" + ((ulong)data.PayloadByName("Irp")).ToString("x");
                                                         inputs += "\nFileKey=    \t0x" + ((ulong)data.PayloadByName("FileKey")).ToString("x");

                                                         string outputs = "ElapsedTimeMS=\t" + data.PayloadStringByName("ElapsedTimeMS");
                                                         outputs += "\nDiskServiceTimeMS=\t" + data.PayloadStringByName("DiskServiceTimeMSec");


                                                         EventItem ei = new EventItem(data, inputs, "", outputs, "");
                                                         lock (_TKernelEventListsLock)
                                                         {
                                                             _TKernelEventListItems.Add(ei);
                                                         }
                                                         worker.ReportProgress((int)data.EventIndex);
                                                     }
                                                     catch (Exception ex)
                                                     {
                                                         ;
                                                     }
                                                 }
                                             }
                                             else if (data.EventName.StartsWith("DiskIO/FlushInit"))
                                             {
                                                 // DiskIO/FlushInit: (ulong)Irp
                                                 ;
                                             }
                                             else if (data.EventName.StartsWith("DiskIO/FlushBuffers"))
                                             {
                                                 // DiskIO/FlushBuffers: (int)DiskNumber, (Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags)IrpFlags, (ulong)Irp, (double)ElapsedTimeMSec
                                                 ;
                                             }
                                             else
                                             {
                                                 // DiskIo\DriverMajorFunctionCall
                                                 // DiskIo\DriverMajorFunctionReturn
                                                 // DiskIo\DriverCompleteRequest
                                                 // DiskIo\DriverCompleteRequestReturn
                                                 ; // ignore
                                             }
                                         }
                                         else if (data.EventName.StartsWith("DiskFileIOInit"))
                                         {
                                             ;
                                         }
                                         else if (data.EventName.StartsWith("DiskFileIO"))
                                         {
                                             ;
                                         }
                                         else
                                         {
                                             ; // ignore;
                                         }

                                     }
                                     else
                                     {
                                         //[Process,Thread,Image]/DCStart  : THese are associated with previously running processes.
                                         if (data.EventName.StartsWith("Image/DC"))
                                         {
                                             ///if (!data.PayloadByName("PID").ToString().Equals("0"))
                                             {
                                                 /// WaitingForEventStart_ProcsKernel = false;
                                             }
                                              ;
                                         }
                                     }
                                 }
                             };
                         };
                }
                catch
                {
                    ;
                }
                TraceEventSession_ProcsKernel.Source.Process();   // note: this call is sychronous
            }
        }

        void DisableKernelTrace()
        {
            try
            {
                if (kerneleventbgw != null)
                {
                    kerneleventbgw.CancelAsync();
                    kerneleventbgw = null;
                }
            }
            catch { }
            try
            {
                if (TraceEventSession_ProcsKernel != null)
                {
                    TraceEventSession_ProcsKernel.Source.StopProcessing();
                    TraceEventSession_ProcsKernel.Source.Dispose();
                    TraceEventSession_ProcsKernel = null;
                }
            }
            catch { }

        }

        private string Interpret_KernelProcessFlags(Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags flags)
        {
            if ((flags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags.None) != 0)
                return " ( NONE )";
            string s = "( ";
            string prefix = "";
            if ((flags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags.Wow64) != 0)
            {
                s += prefix + "WOW64";
                prefix = " | ";
            }
            if ((flags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags.Protected) != 0)
            {
                s += prefix + "PROTECTED";
                prefix = " | ";
            }
            if ((flags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.ProcessFlags.PackageFullName) != 0)
            {
                s += prefix + "PACKAGEFULLNAME";
                prefix = " | ";
            }
            s += " )";
            return s;
        }

        private string Interpret_KernelFileCreateOptions(Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions option)
        {
            //Some options not in the official list used here, and multiples may be in play
            if (option == Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.NONE)
                    return "( NONE )";

            string s = "0x" + ((uint)option).ToString("x") + " (";
            string prefix = "";
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_ARCHIVE) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_ARCHIVE";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_COMPRESSED) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_COMPRESSED";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_DEVICE) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_DEVICE";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_DIRECTORY) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_DIRECTORY";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_ENCRYPTED) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_ENCRYPTED";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_HIDDEN) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_HIDDEN";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_INTEGRITY_STREAM) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_INTEGRITY_STREAM";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_NORMAL) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_NORMAL";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_NOT_CONTENT_INDEXED) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_NOT_CONTENT_INDEXED";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_NO_SCRUB_DATA) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_NO_SCRUB_DATA";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_OFFLINE) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_OFFLINE";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_READONLY) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_READONLY";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_REPARSE_POINT";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_SPARSE_FILE) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_SPARSE_FILE";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_SYSTEM) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_SYSTEM";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_TEMPORARY) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_TEMPORARY";
                prefix = " | ";
            }
            if ((option & Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateOptions.FILE_ATTRIBUTE_VIRTUAL) != 0)
            {
                s += prefix + "FILE_ATTRIBUTE_VIRTUAL";
                prefix = " | ";
            }
            return s += " )";
        }

        private string Interpret_KernelFileCreateDispositions(Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition dispo)
        {
            string s = "0x" + ((uint)dispo).ToString("x") + " (";
            switch (dispo)
            { 
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition.CREATE_NEW:
                    s +=  "CREATE_NEW";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition.CREATE_ALWAYS:
                    s +=  "CREATE_ALWAYS";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition.OPEN_ALWAYS:
                    s +=  "OPEN_ALWAYS";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition.OPEN_EXISING:
                    s +=  "OPEN_EXISING";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.CreateDisposition.TRUNCATE_EXISTING:
                    s += "TRUNCATE_EXISTING";
                    break;
            }
            s += " )";
            return s;
        }
    
        private string Interpret_KernelFile_IrpFlags(Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags IrpFlags)
        {
            string s = "0x" + ((uint)IrpFlags).ToString("x") + " (";
            string prefix = "";
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.AssociatedIrp) != 0)
            {
                s += prefix + "AssociatedIrp";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.BufferedIO) != 0)
            {
                s += prefix + "BufferedIO";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.Close) != 0)
            {
                s += prefix + "Close";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.Create) != 0)
            {
                s += prefix + "Create";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.DeallocateBuffer) != 0)
            {
                s += prefix + "DeallocateBuffer";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.DeferIOCompletion) != 0)
            {
                s += prefix + "DeferIOCompletion";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.HoldDeviceQueue) != 0)
            {
                s += prefix + "HoldDeviceQueue";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.InputOperation) != 0)
            {
                s += prefix + "InputOperation";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.MountCompletion) != 0)
            {
                s += prefix + "MountCompletion";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.Nocache) != 0)
            {
                s += prefix + "Nocache";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.None) != 0)
            {
                s += prefix + "None";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.ObQueryName) != 0)
            {
                s += prefix + "ObQueryName";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.PagingIo) != 0)
            {
                s += prefix + "PagingIo";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.PriorityMask) != 0)
            {
                s += prefix + "PriorityMask";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.Read) != 0)
            {
                s += prefix + "Read";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.SynchronousApi) != 0)
            {
                s += prefix + "SynchronousApi";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.SynchronousPagingIO) != 0)
            {
                s += prefix + "SynchronousPagingIO";
                prefix = " | ";
            }
            if ((IrpFlags & Microsoft.Diagnostics.Tracing.Parsers.Kernel.IrpFlags.Write) != 0)
            {
                s += prefix + "Write";
                prefix = " | ";
            }
            s += " )";
            return s;
        }
        private string Interpret_KernelFile_Priority(Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority iopriority)
        {
            string s = "0x" + ((uint)iopriority).ToString("x") + " (";
            switch (iopriority)
            {
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Critical:
                    s += "CRITICAL";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.High:
                    s += "HIGH";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Low:
                    s += "LOW";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Max:
                    s += "MAX";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Normal:
                    s += "NORMAL";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Notset:
                    s += "NOTSET";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Reserved0:
                    s += "RESERVED0";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Reserved1:
                    s += "RESERVED1";
                    break;
                case Microsoft.Diagnostics.Tracing.Parsers.Kernel.IOPriority.Verylow:
                    s += "VERYLOW";
                    break;
                default:
                    s += "unknown";
                    break;
            }
            return s + ")";
        }



        private string Interpret_return_rom_win32(ulong code)
        {
            string s;
            switch (code)
            {
                case 0: //ERROR_SUCCESS:
                    s = "Success";
                    break;
                case 2:   // ERROR_FILE_NOT_FOUND:
                    s = "Expected Failure ()";
                    break;
                case 3:   // ERROR_PATH_NOT_FOUND:
                    s = "Expected Failure (ERROR_FILE_NOT_FOUND)";
                    break;
                case 123: // ERROR_INVALID_NAME:
                    s = "Expected Failure (ERROR_INVALID_NAME)";
                    break;
                case 183: // ERROR_ALREADY_EXISTS:
                    s = "Expected Failure (ERROR_ALREADY_EXISTS)";
                    break;
                case 80:  // ERROR_FILE_EXISTS:
                    s = "Expected Failure (ERROR_FILE_EXISTS)";
                    break;
                case 122: // ERROR_INSUFFICIENT_BUFFER:
                    s = "Expected Failure (ERROR_INSUFFICIENT_BUFFER)";
                    break;
                case 234: // ERROR_MORE_DATA:
                    s = "Expected Failure (ERROR_MORE_DATA)";
                    break;
                case 259: // ERROR_NO_MORE_ITEMS:
                    s = "Expected Failure (ERROR_NO_MORE_ITEMS)";
                    break;
                case 18:  // ERROR_NO_MORE_FILES:
                    s = "Expected Failure (ERROR_NO_MORE_FILES)";
                    break;
                case 126: // ERROR_MOD_NOT_FOUND:
                    s = "Expected Failure (ERROR_MOD_NOT_FOUND)";
                    break;
                default:
                    s = "Failure";
                    break;
            }
            s += "\nStatus = 0x" + code.ToString("x");
            return s;
        }
    }
}
