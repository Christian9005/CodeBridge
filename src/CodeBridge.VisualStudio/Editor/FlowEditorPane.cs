#nullable enable
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Controls;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using CodeBridge.Flow;
using EnvDTE;

namespace CodeBridge.VisualStudio.Editor
{
    [ComVisible(true)]
    public class FlowEditorPane : WindowPane, IPersistFileFormat, IVsPersistDocData, Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget
    {
        private readonly FlowEditorFactory _factory;
        private string _fileName = string.Empty;
        private bool _isDirty;
        private bool _loadFailed;
        private readonly FlowEditorControl _editorControl;
        private FlowDocument? _document;

        public FlowEditorPane(FlowEditorFactory factory) : base(null)
        {
            _factory = factory;
            _editorControl = new FlowEditorControl();
            _editorControl.OnDirtyChanged += (s, e) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                IsDirtyProperty = true;
            };
            _editorControl.CodeExported += OnCodeExported;
            _editorControl.NamespaceProvider = GetProjectNamespace;
            Content = _editorControl;
        }

        private Project? FindProject()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(_fileName))
                return null;

            var dte = GetService(typeof(SDTE)) as DTE;
            return dte?.Solution?.FindProjectItem(_fileName)?.ContainingProject;
        }

        private string? GetProjectNamespace()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var project = FindProject();
            return project == null || string.IsNullOrEmpty(project.FullName)
                ? null
                : VisualStudioProjectLocator.GetDefaultNamespace(project, project.FullName);
        }

        /// <summary>Writes the exported C# next to the flow, adds it to the project and opens it.</summary>
        private void OnCodeExported(ExportedCode exported)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var directory = string.IsNullOrEmpty(_fileName) ? Path.GetTempPath() : Path.GetDirectoryName(_fileName)!;
                var name = Path.GetFileNameWithoutExtension(exported.FileName);
                var path = Tour.TourContent.UniquePath(directory, name, ".cs");
                File.WriteAllText(path, exported.Code);

                try
                {
                    FindProject()?.ProjectItems.AddFromFile(path);
                }
                catch (Exception)
                {
                    // SDK-style projects already include files by globbing.
                }

                (GetService(typeof(SDTE)) as DTE)?.ItemOperations.OpenFile(path);

                if (exported.IsConsoleProgram)
                {
                    VsShellUtilities.ShowMessageBox(
                        this,
                        "A project can only have one file with top-level statements. Use this Program.cs in a console project, or remove the existing Program.cs first.",
                        "CodeBridge export",
                        OLEMSGICON.OLEMSGICON_INFO,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                VsShellUtilities.ShowMessageBox(this, "Could not write the exported file: " + ex.Message, "CodeBridge export", OLEMSGICON.OLEMSGICON_WARNING, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
            }
        }

        public bool IsDirtyProperty
        {
            get => _isDirty;
            set
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_isDirty != value)
                {
                    _isDirty = value;
                }
            }
        }

        int IPersistFileFormat.IsDirty(out int pfIsDirty)
        {
            pfIsDirty = _isDirty ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int IsDocDataDirty(out int pfDirty)
        {
            pfDirty = _isDirty ? 1 : 0;
            return VSConstants.S_OK;
        }

        public int GetClassID(out Guid pClassID)
        {
            pClassID = PackageGuids.FlowEditorFactory;
            return VSConstants.S_OK;
        }

        public int Load(string pszFilename, uint grfMode, int fReadOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _fileName = pszFilename ?? string.Empty;
            _editorControl.FlowFilePath = _fileName;
            _loadFailed = false;

            try
            {
                if (File.Exists(pszFilename))
                {
                    var json = File.ReadAllText(pszFilename);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        _document = new FlowDocument { Id = Guid.NewGuid().ToString(), Name = Path.GetFileNameWithoutExtension(pszFilename) };
                    }
                    else
                    {
                        var doc = JsonSerializer.Deserialize<FlowDocument>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (doc == null)
                        {
                            throw new InvalidDataException("Parsed FlowDocument is null.");
                        }
                        var problem = FlowDocumentChecks.Problem(doc);
                        if (problem != null)
                        {
                            throw new InvalidDataException(problem);
                        }

                        _document = doc;
                    }
                }
                else
                {
                    _document = new FlowDocument { Id = Guid.NewGuid().ToString(), Name = Path.GetFileNameWithoutExtension(pszFilename) };
                }
            }
            catch (Exception ex)
            {
                return FailLoad(pszFilename, ex.Message);
            }

            if (_document != null)
            {
                try
                {
                    _editorControl.LoadDocument(_document);
                }
                catch (Exception ex)
                {
                    // whatever is wrong with the file, the exception must not travel up into Visual Studio
                    return FailLoad(pszFilename, ex.Message);
                }
            }
            IsDirtyProperty = false;
            
            IVsWindowFrame? frame = (IVsWindowFrame?)GetService(typeof(SVsWindowFrame));
            if (frame != null)
            {
                frame.SetProperty((int)__VSFPROPID.VSFPROPID_EditorCaption, "");
            }

            return VSConstants.S_OK;
        }

        private int FailLoad(string pszFilename, string reason)
        {
            _loadFailed = true;
            VsShellUtilities.ShowMessageBox(
                this,
                $"Failed to load CodeBridge Flow file '{Path.GetFileName(pszFilename)}':\n\n{reason}\n\nThe file will not be overwritten.",
                "CodeBridge Flow Editor Error",
                OLEMSGICON.OLEMSGICON_CRITICAL,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);

            return VSConstants.E_FAIL;
        }

        public int Save(string pszFilename, int fRemember, uint nFormatIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_loadFailed)
            {
                VsShellUtilities.ShowMessageBox(
                    this,
                    "Cannot save because the document previously failed to load properly. Please fix formatting errors in the file before saving.",
                    "Save Aborted",
                    OLEMSGICON.OLEMSGICON_WARNING,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return VSConstants.E_ABORT;
            }

            try
            {
                _document = _editorControl.GetDocument();
                var json = JsonSerializer.Serialize(_document, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(pszFilename, json);
                _editorControl.ClearDirty();
                if (fRemember != 0)
                {
                    _fileName = pszFilename ?? string.Empty;
                    IsDirtyProperty = false;
                }
                return VSConstants.S_OK;
            }
            catch (Exception ex)
            {
                VsShellUtilities.ShowMessageBox(
                    this,
                    $"Error saving file '{Path.GetFileName(pszFilename)}':\n\n{ex.Message}",
                    "Save Error",
                    OLEMSGICON.OLEMSGICON_CRITICAL,
                    OLEMSGBUTTON.OLEMSGBUTTON_OK,
                    OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                return VSConstants.E_FAIL;
            }
        }

        public int LoadDocData(string pszMkDocument)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Load(pszMkDocument, 0, 0);
        }

        public int SaveDocData(VSSAVEFLAGS dwSave, out string pbstrMkDocumentNew, out int pfSaveCanceled)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            pbstrMkDocumentNew = _fileName;
            pfSaveCanceled = 0;
            return Save(_fileName, 1, 0);
        }

        // ---- Edit menu integration (Undo/Redo/Cut/Copy/Paste/Delete/Select All) ----

        int Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget.QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97 || _editorControl.IsTextInputFocused)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

            for (var i = 0; i < cCmds; i++)
            {
                bool? enabled = null;
                switch ((VSConstants.VSStd97CmdID)prgCmds[i].cmdID)
                {
                    case VSConstants.VSStd97CmdID.Undo: enabled = _editorControl.CanUndo; break;
                    case VSConstants.VSStd97CmdID.Redo: enabled = _editorControl.CanRedo; break;
                    case VSConstants.VSStd97CmdID.Copy:
                    case VSConstants.VSStd97CmdID.Cut:
                    case VSConstants.VSStd97CmdID.Delete: enabled = _editorControl.HasSelection; break;
                    case VSConstants.VSStd97CmdID.Paste: enabled = _editorControl.CanPaste; break;
                    case VSConstants.VSStd97CmdID.SelectAll: enabled = true; break;
                }

                if (enabled.HasValue)
                {
                    prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | (enabled.Value ? OLECMDF.OLECMDF_ENABLED : 0));
                }
            }

            return VSConstants.S_OK;
        }

        int Microsoft.VisualStudio.OLE.Interop.IOleCommandTarget.Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (pguidCmdGroup != VSConstants.GUID_VSStandardCommandSet97 || _editorControl.IsTextInputFocused)
                return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;

            switch ((VSConstants.VSStd97CmdID)nCmdID)
            {
                case VSConstants.VSStd97CmdID.Undo: _editorControl.Undo(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.Redo: _editorControl.Redo(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.Copy: _editorControl.CopySelection(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.Cut: _editorControl.Cut(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.Paste: _editorControl.Paste(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.Delete: _editorControl.DeleteSelection(); return VSConstants.S_OK;
                case VSConstants.VSStd97CmdID.SelectAll: _editorControl.SelectAll(); return VSConstants.S_OK;
            }

            return (int)Microsoft.VisualStudio.OLE.Interop.Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int SaveCompleted(string pszFilename) => VSConstants.S_OK;
        public int GetCurFile(out string ppszFilename, out uint pnFormatIndex)
        {
            ppszFilename = _fileName;
            pnFormatIndex = 0;
            return VSConstants.S_OK;
        }
        public int InitNew(uint nFormatIndex) => VSConstants.S_OK;
        public int GetFormatList(out string ppszFormatList)
        {
            ppszFormatList = "CodeBridge Flow (*.cbflow)\n*.cbflow\n";
            return VSConstants.S_OK;
        }
        public int SetDocDataDirty(int fDirty)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            IsDirtyProperty = fDirty != 0;
            return VSConstants.S_OK;
        }
        public int SetUntitledDocPath(string pszDocDataPath) => VSConstants.S_OK;
        public int GetDocData(out IntPtr ppDocData)
        {
            ppDocData = Marshal.GetIUnknownForObject(this);
            return VSConstants.S_OK;
        }
        public int RenameDocData(uint grfAttribs, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
        public int IsDocDataReloadable(out int pfReloadable)
        {
            pfReloadable = 1;
            return VSConstants.S_OK;
        }
        public int ReloadDocData(uint grfFlags)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            return Load(_fileName, 0, 0);
        }
        public int OnRegisterDocData(uint docCookie, IVsHierarchy pHierNew, uint itemidNew) => VSConstants.S_OK;
        public int Close() => VSConstants.S_OK;
        public int GetGuidEditorType(out Guid pClassID)
        {
            pClassID = PackageGuids.FlowEditorFactory;
            return VSConstants.S_OK;
        }
        public int IsDocDataReadOnly(out int pfReadOnly)
        {
            pfReadOnly = 0;
            return VSConstants.S_OK;
        }
    }
}
