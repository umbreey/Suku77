using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace Umbreey
{
    public class Download
    {
        public static string UMBREEY_DOWNLOAD_PATH = $"{Application.persistentDataPath}/UmbreeyDownload";
        public BundleMap BundleMap { get; private set; }
        public string BundleMapTxt { get; private set; } = string.Empty;
        public ResultData Result { get; private set; }
        public Action<Download> OnFinish;
        public Action<ErrorData> OnError;
        public Action<ProgressData> OnProgress;

        public Action<GetBundleMapTrackData> OnTrackGetBundleMapStart;
        public Action<GetBundleMapTrackData> OnTrackGetBundleMapRetry;
        public Action<GetBundleMapTrackData> OnTrackGetBundleMapFinish;
        public Action<SetFormatDownloadDirTrackData> OnTrackSetFormatDownloadDir;
        
        #region 下载内容
        public class ResultData
        {
            public ResultCode code;
            public long httpCode;
            public List<ResultFile> files = new List<ResultFile>();
            public long size; //需要下载的文件大小
            public int buildId;
            public string errorType;
            public string errorMessage;
        }
        public enum ResultCode
        {
            OK = 0, //成功
            ERROR = 1, //访问下载文件失败
            NO_EXIST = 2, //访问下载文件不存在
        }
        
        public class ResultFile
        {
            public string path;
            public string id;
            public string md5;
            public string ext;
        }
        #endregion

        #region 下载进度
        public struct ProgressData
        {
            public long downloadSize; //已下载的文件大小
            public long downloadSizeAll;//总需要下载的文件大小
            public long downloadSpeed;//每秒下载速度
        }
        #endregion

        #region 下载错误
        public enum DownloadCode
        {
            NET_ERROR = 0, // 下载的文件出错误，需要重新下载
            FILE_ERROR, // 文件损坏，需要重新下载
        }
        public struct ErrorData
        {
            public DownloadCode code;
            public string filename; // 错误文件
        }

        public struct GetBundleMapTrackData
        {
            public string url;
            public long responseCode;
            public string requestResult;
            public string requestError;
            public int retry;
            public bool isTimeout;
        }

        public struct SetFormatDownloadDirTrackData
        {
            public string phase;
            public int lineCount;
            public int dirCount;
            public int deletedDirCount;
            public int fileCount;
            public int reservedCount;
            public int deletedOrphanCount;
            public int deletedMismatchCount;
            public int downloadCount;
            public string targetName;
            public string exceptionType;
            public string exceptionMessage;
        }

        #endregion
        
        private int DOWNLOAD_ERROR_COUNT = 3;
        private int BUNDLE_MAP_TIMEOUT = 3; // seconds
        private long DOWNLOAD_MAX_SIZE = 1024 * 1024 * 5;
        private long DOWNLOAD_MAX_COUNT = 10;
        private long _downloadSizeAll = 0;
        private long _currentdownloadSize = 0;
        private string _bundleCommonUrl;
        private float _lastSpeedCheckTime = 0f;
        private long _lastSpeedCheckSize = 0;
        private long _lastSpeedPerSecond = 0;
        private List<DownloadData> _downloadUrls = new List<DownloadData>();
        private List<DownloadData> _completeUrls = new List<DownloadData>();
        
        private class DownloadData
        {
            public string url;
            public string id;
            public string md5;
            public string ext;
            public int size;
            public string filename;
            public string savePath;
            public UnityWebRequest request;
            public int errorCount;
        }

        public Download()
        {
            try
            {
                if(!Directory.Exists(UMBREEY_DOWNLOAD_PATH))
                    Directory.CreateDirectory(UMBREEY_DOWNLOAD_PATH);
            }
            catch (Exception e)
            {
                Log.E($"[Download] CreateDirectory error: {e.Message}. path:{UMBREEY_DOWNLOAD_PATH}");
            }
        }
        
        public void GetDownloadData(string bundleMapUrl, Action<Download> callback)
        {
            var filename = Path.GetFileName(bundleMapUrl);
            _bundleCommonUrl = bundleMapUrl.Replace(filename, "");
            
            Umbreey.Inst.StartCoroutine(GetBundleMap(bundleMapUrl, (request) =>
            {
                Result = new ResultData() { httpCode = request.responseCode };
                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        BundleMapTxt = request.downloadHandler.text;
                        BundleMap = new BundleMap(BundleMapTxt);
                        Result.buildId = BundleMap.Content.Meta.Build;
                        SetFormatDownloadDir(BundleMap.Lines);
                        
                        foreach (var url in _downloadUrls)
                        {
                            Result.size += url.size;
                        }
                        _downloadSizeAll = Result.size;
                        foreach (var (_, line) in BundleMap.Lines)
                        {
                            Result.files.Add(new ResultFile()
                            {
                                path = Path.Combine(UMBREEY_DOWNLOAD_PATH, $"{line.name}"),
                                id = line.id,
                                md5 = line.md5,
                                ext = line.ext,
                            });
                        }
                        Result.code = ResultCode.OK;
                    }
                    catch (Exception e)
                    {
                        Log.E(e);
                        Result.code = ResultCode.ERROR;
                        Result.errorType = e.GetType().Name;
                        Result.errorMessage = e.Message;
                    }
                }
                else if (request.result != UnityWebRequest.Result.ConnectionError &&
                    request.responseCode != 200)
                {
                    Result.code = ResultCode.NO_EXIST;
                    Result.errorType = request.result.ToString();
                    Result.errorMessage = request.error;
                }
                else
                {
                    Result.code = ResultCode.ERROR;
                    Result.errorType = request.result.ToString();
                    Result.errorMessage = request.error;
                }

                callback?.Invoke(this);
            }));
        }

        public void StartDownload()
        {
            _currentdownloadSize = 0;
            _lastSpeedCheckTime = Time.time;
            TaskManager.Remove(Update);
            TaskManager.Update(Update);
        }

        public void StopDownload()
        {
            TaskManager.Remove(Update);
            foreach (var download in _downloadUrls)
            {
                download.request?.downloadHandler?.Dispose();
                download.request?.Dispose();
                download.request = null;
            }
            _downloadUrls.Clear();
            _completeUrls.Clear();
        }
        
        void Update()
        {
            int tryDownloadCount = 0;
            int tryDownloadSize = 0;
            int realdownloadSize = 0;
            _completeUrls.Clear();
            
            for (int i = 0; i < _downloadUrls.Count; i++)
            {
                var data = _downloadUrls[i];
                if (tryDownloadSize < DOWNLOAD_MAX_SIZE &&
                    tryDownloadCount < DOWNLOAD_MAX_COUNT)
                {
                    if (data.request == null)
                    {
                        data.request = UnityWebRequest.Get(data.url);
                        data.request.downloadHandler = new DownloadHandlerFile(data.savePath);
                        data.request.SendWebRequest();
                    }
                    if (data.request.isDone)
                    {
                        bool downloadVerify = data.request.result == UnityWebRequest.Result.Success;
                        bool fileVerify = false;
                        try
                        {
                            fileVerify = File.Exists(data.savePath) &&
                                         FileUtils.GetFileSize(data.savePath) == data.size;
                        }
                        catch (Exception e)
                        {
                            Log.E($"FileVerify failed: {e.Message}");
                        }
                        
                        data.request.downloadHandler.Dispose();
                        data.request.Dispose();
                        data.request = null;
                        if (downloadVerify && fileVerify)
                        {
                            //file ok
                            tryDownloadSize = Mathf.Max(0, tryDownloadSize - data.size);
                            _currentdownloadSize += data.size;
                            _completeUrls.Add(data);
                        }
                        else
                        {
                            //file error
                            try
                            {
                                if (File.Exists(data.savePath))
                                    File.Delete(data.savePath);
                            }
                            catch (Exception e)
                            {
                                Log.E($"Delete failed: {data.savePath}, {e.Message}");
                            }

                            data.errorCount++;
                            if (data.errorCount >= DOWNLOAD_ERROR_COUNT)
                            {
                                TaskManager.Remove(Update);
                                ErrorData errorData = default(ErrorData);
                                errorData.filename = data.filename;
                                if (!downloadVerify)
                                {
                                    errorData.code = DownloadCode.NET_ERROR;
                                }
                                else if (!fileVerify)
                                {
                                    errorData.code = DownloadCode.FILE_ERROR;
                                }
                                OnError?.Invoke(errorData);
                                return;
                            }
                        }
                    }
                    else
                    {
                        tryDownloadCount++;
                        tryDownloadSize += data.size;
                        realdownloadSize+=(int)data.request.downloadedBytes;
                    }
                }
                else
                    break;
            }

            // 删除已完成的urls
            foreach (var data in _completeUrls)
            {
                _downloadUrls.Remove(data);
            }

            // 统计下载信息
            var currentSize = realdownloadSize + _currentdownloadSize;
            if (_downloadSizeAll > 0)
            {
                if (Time.time - _lastSpeedCheckTime >= 1f)
                {
                    _lastSpeedPerSecond =Math.Max(1024,(currentSize - _lastSpeedCheckSize));
                    _lastSpeedCheckSize = currentSize;
                    _lastSpeedCheckTime = Time.time;
                }
                OnProgress?.Invoke(new ProgressData()
                {
                    downloadSpeed = _lastSpeedPerSecond,
                    downloadSize = currentSize,
                    downloadSizeAll = _downloadSizeAll
                });
            }
            if (_downloadUrls.Count == 0)
            {
                TaskManager.Remove(Update);
                OnFinish?.Invoke(this);
            }
        }
        
        IEnumerator GetBundleMap(string url, Action<UnityWebRequest> callback)
        {
            OnTrackGetBundleMapStart?.Invoke(new GetBundleMapTrackData()
            {
                url = url,
                responseCode = 0,
                requestResult = string.Empty,
                requestError = string.Empty,
            });
            
            for (int retryCount = 0; retryCount < DOWNLOAD_ERROR_COUNT; retryCount++)
            {
                using (UnityWebRequest request = UnityWebRequest.Get(url))
                {
                    request.timeout = BUNDLE_MAP_TIMEOUT;
                    var asyncOp = request.SendWebRequest();
                    
                    float expireTime = Time.realtimeSinceStartup + BUNDLE_MAP_TIMEOUT;
                    yield return new WaitUntil(() => asyncOp.isDone || Time.realtimeSinceStartup > expireTime);

                    bool isSuccess = asyncOp.isDone && request.result == UnityWebRequest.Result.Success;
                    bool isLastTry = retryCount == DOWNLOAD_ERROR_COUNT - 1;
                    var trackData = new GetBundleMapTrackData() {
                        url = url,
                        responseCode = request.responseCode,
                        requestResult = request.result.ToString(),
                        requestError = request.error ?? string.Empty,
                        retry = retryCount,
                    };
                    
                    if (isSuccess || isLastTry)
                    {
                        OnTrackGetBundleMapFinish?.Invoke(trackData);
                        callback?.Invoke(request);
                        yield break;
                    }
                    
                    if (!asyncOp.isDone)
                    {
                        request.Abort();
                        Log.E($"[Download] GetBundleMap timeout abort. url:{url} retry:{retryCount}");

                        trackData.isTimeout = true;
                    }
                    else if (request.result != UnityWebRequest.Result.Success)
                    {
                        Log.E($"[Download] GetBundleMap failure. code:{request.responseCode} error:{request.error} url:{url} retry:{retryCount}");
                    }
                    OnTrackGetBundleMapRetry?.Invoke(trackData);
                }
                
                yield return new WaitForSeconds(0.5f);
            }
        }
        
        void SetFormatDownloadDir(Dictionary<string, BundleMap.LineData> lines)
        {
            var reserved = new HashSet<string>();
            var dirs = Array.Empty<string>();
            var files = Array.Empty<string>();
            var deletedDirCount = 0;
            var deletedOrphanCount = 0;
            var deletedMismatchCount = 0;
            string currentTargetName = string.Empty;

            OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
            {
                phase = "start",
                lineCount = lines?.Count ?? 0,
                dirCount = 0,
                deletedDirCount = 0,
                fileCount = 0,
                reservedCount = 0,
                deletedOrphanCount = 0,
                deletedMismatchCount = 0,
                downloadCount = 0,
                targetName = string.Empty,
                exceptionType = string.Empty,
                exceptionMessage = string.Empty,
            });

            try
            {
                dirs = Directory.GetDirectories(UMBREEY_DOWNLOAD_PATH);
                foreach (var dir in dirs)
                {
                    currentTargetName = Path.GetFileName(dir);
                    Directory.Delete(dir, true);
                    deletedDirCount++;
                }
            }
            catch (Exception e)
            {
                Log.E($"[Download] SetFormatDownloadDir delete dirs error: {e.Message}");
                OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
                {
                    phase = "cleanup_dirs_error",
                    lineCount = lines?.Count ?? 0,
                    dirCount = dirs.Length,
                    deletedDirCount = deletedDirCount,
                    fileCount = 0,
                    reservedCount = reserved.Count,
                    deletedOrphanCount = 0,
                    deletedMismatchCount = 0,
                    downloadCount = 0,
                    targetName = currentTargetName ?? string.Empty,
                    exceptionType = e.GetType().Name,
                    exceptionMessage = e.Message ?? string.Empty,
                });
                throw;
            }

            OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
            {
                phase = "cleanup_dirs_finish",
                lineCount = lines?.Count ?? 0,
                dirCount = dirs.Length,
                deletedDirCount = deletedDirCount,
                fileCount = 0,
                reservedCount = 0,
                deletedOrphanCount = 0,
                deletedMismatchCount = 0,
                downloadCount = 0,
                targetName = string.Empty,
                exceptionType = string.Empty,
                exceptionMessage = string.Empty,
            });

            try
            {
                files = Directory.GetFiles(UMBREEY_DOWNLOAD_PATH);
                foreach (var file in files)
                {
                    var fileName = Path.GetFileName(file);
                    currentTargetName = fileName;
                    var fileSize = FileUtils.GetFileSize(file);

                    BundleMap.LineData l = null;
                    foreach (var (_, line) in lines)
                    {
                        if (line.name == fileName)
                        {
                            l = line;
                            break;
                        }
                    }

                    if (l == null)
                    {
                        File.Delete(file);
                        deletedOrphanCount++;
                        Log.D($"[Download] delete {fileName} because it's not found in hotupdate lines");
                    }
                    else if (fileSize != l.size)
                    {
                        File.Delete(file);
                        deletedMismatchCount++;
                        Log.D($"[Download] delete {fileName} because its size mismatch {fileSize} != {l.size}");
                    }
                    else
                    {
                        reserved.Add(fileName);
                        Log.D($"[Download] reserve {fileName}");
                    }
                }
            }
            catch (Exception e)
            {
                Log.E($"[Download] SetFormatDownloadDir file scan error: {e.Message}");
                OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
                {
                    phase = "scan_files_error",
                    lineCount = lines?.Count ?? 0,
                    dirCount = dirs.Length,
                    deletedDirCount = deletedDirCount,
                    fileCount = files.Length,
                    reservedCount = reserved.Count,
                    deletedOrphanCount = deletedOrphanCount,
                    deletedMismatchCount = deletedMismatchCount,
                    downloadCount = 0,
                    targetName = currentTargetName ?? string.Empty,
                    exceptionType = e.GetType().Name,
                    exceptionMessage = e.Message ?? string.Empty,
                });
                throw;
            }

            OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
            {
                phase = "scan_files_finish",
                lineCount = lines?.Count ?? 0,
                dirCount = dirs.Length,
                deletedDirCount = deletedDirCount,
                fileCount = files.Length,
                reservedCount = reserved.Count,
                deletedOrphanCount = deletedOrphanCount,
                deletedMismatchCount = deletedMismatchCount,
                downloadCount = 0,
                targetName = string.Empty,
                exceptionType = string.Empty,
                exceptionMessage = string.Empty,
            });

            _completeUrls = new List<DownloadData>();
            _downloadUrls = new List<DownloadData>();
            foreach (var (_, line) in lines)
            {
                var filename = line.name;
                if (!reserved.Contains(filename))
                {
                    Log.D($"[Download] new {filename}");
                    _downloadUrls.Add(new DownloadData()
                    {
                        filename = filename,
                        url = Path.Combine(_bundleCommonUrl, filename),
                        savePath = Path.Combine(UMBREEY_DOWNLOAD_PATH, filename),
                        id = line.id,
                        md5 = line.md5,
                        ext = line.ext,
                        size = line.size,
                    });
                }
            }

            OnTrackSetFormatDownloadDir?.Invoke(new SetFormatDownloadDirTrackData()
            {
                phase = "finish",
                lineCount = lines?.Count ?? 0,
                dirCount = dirs.Length,
                deletedDirCount = deletedDirCount,
                fileCount = files.Length,
                reservedCount = reserved.Count,
                deletedOrphanCount = deletedOrphanCount,
                deletedMismatchCount = deletedMismatchCount,
                downloadCount = _downloadUrls.Count,
                targetName = string.Empty,
                exceptionType = string.Empty,
                exceptionMessage = string.Empty,
            });
        }
    }
}
