﻿using AutoMapper;
using CurlSharp;
using Jackett.Models;
using Jackett.Services;
using NLog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Jackett.Utils.Clients
{
    public class UnixSafeCurlWebClient : IWebClient
    {
        private IProcessService processService;
        private Logger logger;

        public UnixSafeCurlWebClient(IProcessService p, Logger l)
        {
            processService = p;
            logger = l;
        }

        public void Init()
        {
        }

        public async Task<WebClientByteResult> GetBytes(WebRequest request)
        {
            logger.Debug(string.Format("UnixSafeCurlWebClient:GetBytes(Url:{0})", request.Url));
            var result = await Run(request);
            logger.Debug(string.Format("UnixSafeCurlWebClient: Returning {0} => {1} bytes", result.Status, (result.Content == null ? "<NULL>" : result.Content.Length.ToString())));
            return result;
        }

        public async Task<WebClientStringResult> GetString(WebRequest request)
        {
            logger.Debug(string.Format("UnixSafeCurlWebClient:GetString(Url:{0})", request.Url));
            var result = await Run(request);
            logger.Debug(string.Format("UnixSafeCurlWebClient: Returning {0} => {1}", result.Status, (result.Content == null ? "<NULL>" : Encoding.UTF8.GetString(result.Content))));
            return Mapper.Map<WebClientStringResult>(result);
        }

        private async Task<WebClientByteResult> Run(WebRequest request)
        {
            var args = new StringBuilder();
            args.AppendFormat("--url \"{0}\" ", request.Url);
            args.AppendFormat("-i  -sS --user-agent \"{0}\" ", BrowserUtil.ChromeUserAgent);

            if (!string.IsNullOrWhiteSpace(request.Cookies))
            {
                args.AppendFormat("--cookie \"{0}\" ", request.Cookies);
            }

            if (!string.IsNullOrWhiteSpace(request.Referer))
            {
                args.AppendFormat("--referer \"{0}\" ", request.Referer);
            }

            if (request.PostData != null && request.PostData.Count() > 0)
            {
                var postString = StringUtil.PostDataFromDict(request.PostData);
                args.AppendFormat("--data \"{0}\" ", postString);
            }

            var tempFile = Path.GetTempFileName();
            args.AppendFormat("--output \"{0}\" ", tempFile);

            if (Startup.DoSSLFix == true)
            {
                // http://stackoverflow.com/questions/31107851/how-to-fix-curl-35-cannot-communicate-securely-with-peer-no-common-encryptio
                // https://git.fedorahosted.org/cgit/mod_nss.git/plain/docs/mod_nss.html
                args.Append("--cipher " + SSLFix.CipherList);
            }

            string stdout = null;
            await Task.Run(() =>
            {
                stdout = processService.StartProcessAndGetOutput(System.Environment.OSVersion.Platform == PlatformID.Unix ? "curl" : "curl.exe", args.ToString(), true);
            });

            var outputData = File.ReadAllBytes(tempFile);
            File.Delete(tempFile);
            stdout = Encoding.UTF8.GetString(outputData);
            var result = new WebClientByteResult();
            var headSplit = stdout.IndexOf("\r\n\r\n");
            if (headSplit < 0)
                throw new Exception("Invalid response");
            var headers = stdout.Substring(0, headSplit);
            var headerCount = 0;
            foreach (var header in headers.Split(new char[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (headerCount == 0)
                {
                    var responseCode = int.Parse(header.Split(' ')[1]);
                    result.Status = (HttpStatusCode)responseCode;
                }
                else
                {
                    var headerSplitIndex = header.IndexOf(':');
                    if (headerSplitIndex > 0)
                    {
                        var name = header.Substring(0, headerSplitIndex).ToLowerInvariant();
                        var value = header.Substring(headerSplitIndex + 1);
                        switch (name)
                        {
                            case "set-cookie":
                                var cookieDataSplit = value.IndexOf(';');
                                if (cookieDataSplit > 0)
                                {
                                    result.Cookies += value.Substring(0, cookieDataSplit + 1) + " ";
                                }//Location
                                break;
                            case "location":
                                result.RedirectingTo = value.Trim();
                                break;
                        }
                    }
                }
                headerCount++;
            }

            result.Content = new byte[outputData.Length - (headSplit + 3)];
            var dest = 0;
            for (int i = headSplit + 4; i < outputData.Length; i++)
            {
                result.Content[dest] = outputData[i];
                dest++;
            }

            logger.Debug("WebClientByteResult returned " + result.Status);
            ServerUtil.ResureRedirectIsFullyQualified(request, result);
            return result;
        }
    }
}
