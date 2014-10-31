using System;
using System.IO;
using System.Linq;
using System.Web;
using Ionic.Zip;
using log4net;
using log4net.Core;
using log4net.Ext.EventID;

namespace SuperIndex
{
    static class Extensions
    {
        public static string Plusified(this string uri)
        {
            return uri.Replace("%20", "+").Replace(' ', '+');
        }
    }

    public class Request
    {
        public string RootUri { get; private set; }
        public string RootPath { get; private set; }
        public string Uri { get; private set; }
        public string Path { get; private set; }

        public Request(string rootUri, string physRootPath, string uri, string physPath)
        {
            RootUri = rootUri;
            if (!RootUri.EndsWith("/"))
                RootUri = RootUri + "/";
            RootPath = physRootPath;
            Uri = uri.Plusified();
            Path = physPath;
        }

        public Request(HttpRequest rq)
            : this(rq.ApplicationPath, rq.PhysicalApplicationPath, rq.CurrentExecutionFilePath, rq.PhysicalPath)
        {
        }

        public bool IsRoot
        {
            get { return Uri.Equals(RootUri); }
        }

        public string GetResourceUri(string resource)
        {
            return RootUri + ".res/" + HttpUtility.UrlPathEncode(resource).Plusified();
        }

        public string GetFileUri(string fileName)
        {
            return Uri + HttpUtility.UrlPathEncode(fileName).Plusified();
        }

        public string GetFilePathUri(string filePath)
        {
            return Uri + HttpUtility.UrlPathEncode(System.IO.Path.GetFileName(filePath)).Plusified();
        }

        public string GetDirUri(string subDirName)
        {
            return Uri + HttpUtility.UrlPathEncode(subDirName).Plusified() + "/";
        }
    }

    public class TiledPage : IHttpHandler
    {
        private static readonly ILog Log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private const int DefaultPageSize = 50;

        public void ProcessRequest(HttpContext context)
        {
            Log.Debug("******************************");
            Log.Info("Url: " + context.Request.Url);
            Log.Debug("AppRelativeCurrentExecutionFilePath: " + context.Request.AppRelativeCurrentExecutionFilePath);
            Log.Debug("ApplicationPath: " + context.Request.ApplicationPath);
            Log.Debug("CurrentExecutionFilePath: " + context.Request.CurrentExecutionFilePath);
            Log.Debug("CurrentExecutionFilePathExtension: " + context.Request.CurrentExecutionFilePathExtension);
            Log.Debug("FilePath: " + context.Request.FilePath);
            Log.Debug("Path: " + context.Request.Path);
            Log.Debug("PhysicalApplicationPath: " + context.Request.PhysicalApplicationPath);
            Log.Debug("PhysicalPath: " + context.Request.PhysicalPath);

            if (!context.Request.CurrentExecutionFilePath.EndsWith("/"))
            {
                context.Response.Redirect(context.Request.CurrentExecutionFilePath + "/", true);
                return;
            }

            var request = new Request(context.Request);

            var format = context.Request.Params["format"] ?? String.Empty;
            if (format.Equals("zip", StringComparison.InvariantCultureIgnoreCase))
            {
                CreateZip(context.Request.PhysicalPath, context);
                return;
            }

            var pageString = context.Request.Params["page"];
            int page;
            if (!int.TryParse(pageString, out page))
                page = 0;

            var pageSizeString = context.Request.Params["pageSize"];
            int pageSize;
            if (!int.TryParse(pageSizeString, out pageSize))
                pageSize = DefaultPageSize;

            ProcessDirectory(request, page, pageSize, context);
        }

        private static void CreateZip(string physicalPath, HttpContext context)
        {
            bool tar = context.Request.Params.AllKeys.Contains("tar");

            context.Response.ContentType = "application/zip";
            context.Response.AppendHeader("content-disposition", "attachment; filename=" + Path.GetFileName(physicalPath) + ".zip");
            context.Response.AppendHeader("Transfer-Encoding", "chunked");

            Log.Debug("Zip!");
            using (var zip = new ZipFile())
            {
                if (tar)
                    zip.CompressionLevel = Ionic.Zlib.CompressionLevel.None;

                // filesToInclude is a string[] or List<string>
                if (File.Exists(physicalPath))
                {
                    zip.AddFile(physicalPath, String.Empty);
                    Log.Debug("Added file!");
                }
                else if (Directory.Exists(physicalPath))
                {
                    AddDirectoryToZip(zip, physicalPath, String.Empty, context);
                    Log.Debug("Added Dir!");
                }
                else
                {
                    context.Response.StatusCode = 404;
                    return;
                }

                Log.Debug("Saving Zip!");
                zip.Save(context.Response.OutputStream);
                Log.Debug("Saved Zip!");
                context.Response.OutputStream.Close();
            }
            context.Response.Close();
        }

        private static void AddDirectoryToZip(ZipFile zip, string physicalPath, string destFolder, HttpContext context)
        {
            bool nested = context.Request.Params.AllKeys.Contains("nest");
            var dirInfo = new DirectoryInfo(physicalPath);
            var files = dirInfo.GetFiles().Where(fi => !Hide(fi, context)).OrderBy(fi => fi.Name);
            zip.AddFiles(files.Select(fi => fi.FullName), destFolder);
            if (nested)
            {
                var subDirs = dirInfo.GetDirectories().Where(di => !Hide(di, context)).OrderByDescending(di => di.LastWriteTimeUtc);
                foreach (var subDir in subDirs)
                {
                    var relativePath = string.IsNullOrEmpty(destFolder) ? subDir.Name : Path.Combine(destFolder, subDir.Name);
                    AddDirectoryToZip(zip, subDir.FullName, relativePath, context);
                }
            }
        }

        private static string GetIndexLink(string fullUri, int pageSize)
        {
            return pageSize != DefaultPageSize ? string.Format("{0}?pageSize={1}", fullUri, pageSize) : fullUri;
        }

        private static string GetIndexLink(string fullUri, int page, int pageSize)
        {
            return pageSize != DefaultPageSize ? string.Format("{0}?page={1}&pageSize={2}", fullUri, page, pageSize) : string.Format("{0}?page={1}", fullUri, page);
        }

        private static string GetThumbnailLink(string fullUri, int size)
        {
            return string.Format("{0}?w={1}&h={1}&mode=Box", fullUri, size);
        }

        private static string UpDiv(Request request, int pageSize)
        {
            var link = new HtmlString(pageSize == DefaultPageSize ? "../" : string.Format("../?pageSize={0}",pageSize));
            var name = new HtmlString("..");

            var tnLink = new HtmlString(request.GetResourceUri("folder.png"));
            var extra = string.Format("<a href='{0}'><img class='default' src='{1}'/></a>", link, tnLink);
            return string.Format("<div class='dir'><div class='name'><span><a href='{0}'>{1}</a></span></div><div class='icon'><span>{2}</span></div></div>", link, name, extra);
        }

        private static string PrevPageDiv(Request request, int page, int pageSize)
        {
            var link = new HtmlString(GetIndexLink(request.Uri, page - 1, pageSize));
            var name = new HtmlString(string.Format("Previous {0}", pageSize));

            var tnLink = new HtmlString(request.GetResourceUri("prev.png"));
            var extra = string.Format("<a href='{0}'><img class='default' src='{1}'/></a>", link, tnLink);
            return string.Format("<div class='dir'><div class='name'><span><a href='{0}'>{1}</a></span></div><div class='icon'><span>{2}</span></div></div>", link, name, extra);
        }

        private static string NextPageDiv(Request request, int page, int pageSize, int itemsLeft)
        {
            var link = new HtmlString(GetIndexLink(request.Uri, page + 1, pageSize));
            var name = new HtmlString(string.Format("Next {0}", itemsLeft));

            var tnLink = new HtmlString(request.GetResourceUri("next.png"));
            var extra = string.Format("<a href='{0}'><img class='default' src='{1}'/></a>", link, tnLink);
            return string.Format("<div class='dir'><div class='name'><span><a href='{0}'>{1}</a></span></div><div class='icon'><span>{2}</span></div></div>", link, name, extra);
        }

        private static string DirDiv(string dir, Request request, int pageSize)
        {
            var link = new HtmlString(GetIndexLink(request.GetDirUri(dir), pageSize));
            var name = new HtmlString(Path.GetFileName(dir));

            var tnLink = new HtmlString(request.GetResourceUri("folder.png"));
            var extra = string.Format("<a href='{0}'><img class='default' src='{1}'/></a>", link, tnLink);
            return string.Format("<div class='dir'><div class='name'><span><a href='{0}'>{1}</a></span></div><div class='icon'><span>{2}</span></div></div>", link, name, extra);
        }

        private static string FileDiv(string filePath, Request request)
        {
            var fileName = Path.GetFileName(filePath);
            var fileUri = request.GetFilePathUri(filePath);
            var link = new HtmlString(fileUri);
            var name = new HtmlString(fileName);
            string extra;

            var ext = Path.GetExtension(filePath) ?? String.Empty;
            if (ext.Equals(".mp3", StringComparison.InvariantCultureIgnoreCase)
                || ext.Equals(".aac", StringComparison.InvariantCultureIgnoreCase))
            {
                extra = string.Format("<audio controls preload='none'><source src='{0}'/></audio>", link);
            }
            else if (ext.Equals(".mp4", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".m4v", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".ogv", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".webm", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".mov", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".f4v", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".3g2", StringComparison.InvariantCultureIgnoreCase)
                     || ext.Equals(".3gp", StringComparison.InvariantCultureIgnoreCase))
            {
                var embeddedVidLink = link;
                var videoThumbFilePath = Path.ChangeExtension(filePath, ".THV");
                if (File.Exists(videoThumbFilePath))
                {
                    var vtnUri = request.GetFilePathUri(videoThumbFilePath);
                    var vtnLink = new HtmlString(vtnUri);
                    embeddedVidLink = vtnLink;
                }

                var thumbFilePath = Path.ChangeExtension(filePath, ".THM");
                if (File.Exists(thumbFilePath))
                {
                    //var thumbLink = new HtmlString(GetFileLink(thumbFile, context));
                    var tnUri = request.GetFilePathUri(thumbFilePath);
                    var tnLink = new HtmlString(tnUri);
                    extra = string.Format("<video width='224px' controls preload='none' poster='{1}'><source src='{0}'/></video>", embeddedVidLink, tnLink);
                }
                else
                {
                    extra = string.Format("<video width='224px' controls preload='none'><source src='{0}'/></video>",
                                          embeddedVidLink);
                }
            }
            else if (ext.Equals(".jpg", StringComparison.InvariantCultureIgnoreCase))
            {
                var tnLink = new HtmlString(GetThumbnailLink(fileUri, 224));
                extra = string.Format("<a href='{0}'><img src='{1}'/></a>", link, tnLink);
            }
            else
            {
                var tnLink = new HtmlString(request.GetResourceUri("file.png"));
                extra = string.Format("<a href='{0}'><img class='default' src='{1}'/></a>", link, tnLink);
            }
            //style="float:left;width:300px
            return string.Format("<div class='file'><div class='name'><span><a href='{0}'>{1}</a></span></div><div class='icon'><span>{2}</span></div></div>", link, name, extra);
        }

        private static string GetHeader(Request request)
        {
            var title = new HtmlString(request.Uri);
            var cssLink = new HtmlString(request.GetResourceUri("SuperIndex.css"));
            return
                string.Format(
                    "<head><meta name='HandheldFriendly' content='true' /><meta name='viewport' content='target-densitydpi=device-dpi ,width=device-width, height=device-height, user-scalable=no' /><title>{0} - deBoerisTroef</title><link rel='StyleSheet' href='{1}' type='text/css'></head>",
                    title, cssLink);
        }

        private static bool Hide(DirectoryInfo di, HttpContext context)
        {
            if (context.Request.Params["hidden"] != null)
                return false;
            return di.Name.StartsWith(".") || ((di.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) ||
                   di.Name.Equals("bin", StringComparison.InvariantCultureIgnoreCase);
        }

        private static bool Hide(FileInfo fi, HttpContext context)
        {
            if (context.Request.Params["hidden"] != null)
                return false;
            return fi.Name.StartsWith(".") || ((fi.Attributes & FileAttributes.Hidden) == FileAttributes.Hidden) ||
                   fi.Extension.Equals(".thm", StringComparison.InvariantCultureIgnoreCase) ||
                   fi.Extension.Equals(".thv", StringComparison.InvariantCultureIgnoreCase) ||
                   fi.Extension.Equals(".ashx", StringComparison.InvariantCultureIgnoreCase) ||
                   fi.Name.Equals("web.config", StringComparison.InvariantCultureIgnoreCase);
        }

        private static void ProcessDirectory(Request request, int page, int pageSize, HttpContext context)
        {
            Log.DebugFormat("Dir: {0}, page={1}, size={2}", request.Path, page, pageSize);
            int skip = page*pageSize;
            var dirInfo = new DirectoryInfo(request.Path);
            var subDirs = dirInfo.GetDirectories().Where(di => !Hide(di, context)).OrderByDescending(di => di.Name).ToArray();
            var fileSkip = Math.Max(0, skip - subDirs.Length);
            var dirTake = pageSize;
            var subDirsFromHere = subDirs.Skip(skip).ToArray();
            var takenSubDirs = subDirsFromHere.Take(dirTake).ToArray();
            var fileTake = pageSize - takenSubDirs.Length;
            var filesFromHere = dirInfo.GetFiles().Where(fi => !Hide(fi, context)).OrderBy(fi => fi.Name).Skip(fileSkip).ToArray();
            var dirsLeft = Math.Max(0, subDirs.Length - (skip + dirTake));
            var filesLeft = filesFromHere.Length - fileTake;
            var itemsLeft = dirsLeft + filesLeft;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("<html>");
            sb.AppendLine(GetHeader(request));
            sb.AppendLine("<body><div>");
            if (!request.IsRoot)
                sb.AppendLine(UpDiv(request, pageSize));
            if (skip > 0)
                sb.AppendLine(PrevPageDiv(request, page, pageSize));
            foreach (var subDir in takenSubDirs)
                sb.AppendLine(DirDiv(subDir.Name, request, pageSize));
            foreach (var file in filesFromHere.Take(fileTake))
                sb.AppendLine(FileDiv(file.FullName, request));

            if (itemsLeft > 0)
                sb.AppendLine(NextPageDiv(request, page, pageSize, Math.Min(pageSize, itemsLeft)));

            if (false && filesFromHere.Any())
            {
                var baseLink = request.Uri;
                var tarLink = baseLink + "?format=zip&tar=1";
                var zipLink = baseLink + "?format=zip";
                sb.AppendFormat("<div>Download files as <a href='{0}'>uncompressed</a> or <a href='{1}'>compressed</a> zip archive</div>", tarLink, zipLink);
            }

            sb.AppendLine("</div></body>");
            sb.AppendLine("</html>");
            context.Response.Write(sb.ToString());
        }

        public bool IsReusable
        {
            get { return true; }
        }
    }
}