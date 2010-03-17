﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Hammock.Authentication;
using Hammock.Caching;
using Hammock.Extensions;
using Hammock.Retries;
using Hammock.Web.Mocks;
using Hammock.Serialization;
using Hammock.Tasks;
using Hammock.Web;

namespace Hammock
{
#if !Silverlight
    [Serializable]
#endif
    public class RestClient : RestBase, IRestClient
    {
        public virtual string Authority { get; set; }

#if !Silverlight
        private bool _firstTry = true;
#endif
        private int _remainingRetries;
        private TimedTask _task;

#if !Silverlight
        public virtual RestResponse Request(RestRequest request)
        {
            var query = RequestImpl(request);

            return BuildResponseFromResult(request, query);
        }

        public virtual RestResponse<T> Request<T>(RestRequest request)
        {
            var query = RequestImpl(request);

            return BuildResponseFromResult<T>(request, query);
        }

        private WebQuery RequestImpl(RestRequest request)
        {
            var uri = request.BuildEndpoint(this);
            var query = GetQueryFor(request, uri);
            SetQueryMeta(request, query);

            var retryPolicy = GetRetryPolicy(request);
            if (_firstTry)
            {
                _remainingRetries = (retryPolicy != null ? retryPolicy.RetryCount : 0) + 1;
                _firstTry = false;
            }

            WebQueryResult previous = null;
            while (_remainingRetries > 0)
            {
                var url = uri.ToString();
                if (RequestExpectsMock(request))
                {
                    url = BuildMockRequestUrl(request, query, url);
                }

                WebException exception;
                if (!RequestWithCache(request, query, url, out exception) &&
                    !RequestMultiPart(request, query, url, out exception))
                {
                    query.Request(url, out exception);
                }

                query.Result.Exception = exception;
                query.Result.PreviousResult = previous;
                var current = query.Result;
               
                var retry = false;
                if(retryPolicy != null)
                {
                    foreach(RetryErrorCondition condition in retryPolicy.RetryConditions)
                    {
                        if(exception == null)
                        {
                            continue;
                        }
                        retry |= condition.RetryIf(exception);
                    }

                    if(retry)
                    {
                        previous = current;
                        _remainingRetries--;
                    }
                    else
                    {
                        _remainingRetries = 0;
                    }
                }
                else
                {
                    _remainingRetries = 0;
                }

                query.Result = current;
            }

            _firstTry = _remainingRetries == 0;
            return query;
        }

        private bool RequestMultiPart(RestBase request, WebQuery query, string url, out WebException exception)
        {
            var parameters = GetPostParameters(request);
            if(parameters == null || parameters.Count() == 0)
            {
                exception = null;
                return false;
            }

            // [DC]: Default to POST if no method provided
            query.Method = query.Method != WebMethod.Post && Method != WebMethod.Put ? WebMethod.Post : query.Method;
            query.Request(url, parameters, out exception);
            return true;
        }

        private bool RequestWithCache(RestBase request, WebQuery query, string url, out WebException exception)
        {
            var cache = GetCache(request);
            if (Cache == null)
            {
                exception = null;
                return false;
            }

            var options = GetCacheOptions(request);
            if (options == null)
            {
                exception = null;
                return false;
            }

            // [DC]: This is currently prefixed to the full URL
            var function = GetCacheKeyFunction(request);
            var key = function != null ? function.Invoke() : "";

            switch (options.Mode)
            {
                case CacheMode.NoExpiration:
                    query.Request(url, key, cache, out exception);
                    break;
                case CacheMode.AbsoluteExpiration:
                    var expiry = options.Duration.FromNow();
                    query.Request(url, key, cache, expiry, out exception);
                    break;
                case CacheMode.SlidingExpiration:
                    query.Request(url, key, cache, options.Duration, out exception);
                    break;
                default:
                    throw new NotSupportedException("Unknown CacheMode");
            }

            return true;
        }
#endif
        private string BuildMockRequestUrl(RestRequest request, WebQuery query, string url)
        {
            WebRequest.RegisterPrefix("mock", new MockWebRequestFactory());
            if (url.Contains("https"))
            {
                url = url.Replace("https", "mock");
                query.Parameters.Add("mockScheme", "https");
            }
            if (url.Contains("http"))
            {
                url = url.Replace("http", "mock");
                query.Parameters.Add("mockScheme", "http");
            }

            if (request.ExpectStatusCode.HasValue)
            {
                query.Parameters.Add("mockStatusCode", ((int)request.ExpectStatusCode.Value).ToString());
                if (request.ExpectStatusDescription.IsNullOrBlank())
                {
                    query.Parameters.Add("mockStatusDescription", request.ExpectStatusCode.ToString());
                }
            }
            if (!request.ExpectStatusDescription.IsNullOrBlank())
            {
                query.Parameters.Add("mockStatusDescription", request.ExpectStatusDescription);
            }

            var entity = SerializeExpectEntity(request);
            if (entity != null)
            {
                query.Parameters.Add("mockContent", entity.Content);
                query.Parameters.Add("mockContentType", entity.ContentType);
            }
            else
            {
                if (!request.ExpectContent.IsNullOrBlank())
                {
                    query.Parameters.Add("mockContent", request.ExpectContent);
                    query.Parameters.Add("mockContentType",
                                         !request.ExpectContentType.IsNullOrBlank()
                                             ? request.ExpectContentType
                                             : "text/html"
                        );
                }
                else
                {
                    if (!request.ExpectContentType.IsNullOrBlank())
                    {
                        query.Parameters.Add(
                            "mockContentType", request.ExpectContentType
                            );
                    }
                }
            }

            if (request.ExpectHeaders.Count > 0)
            {
                var names = new StringBuilder();
                var values = new StringBuilder();
                var count = 0;
                foreach (var key in request.ExpectHeaders.AllKeys)
                {
                    names.Append(key);
                    values.Append(request.ExpectHeaders[key]);
                    count++;
                    if (count < request.ExpectHeaders.Count)
                    {
                        names.Append(",");
                        values.Append(",");
                    }
                }

                query.Parameters.Add("mockHeaderNames", names.ToString());
                query.Parameters.Add("mockHeaderValues", values.ToString());
            }
            return url;
        }

        private static bool RequestExpectsMock(RestRequest request)
        {
            return request.ExpectEntity != null ||
                   request.ExpectHeaders.Count > 0 ||
                   request.ExpectStatusCode.HasValue ||
                   !request.ExpectContent.IsNullOrBlank() ||
                   !request.ExpectContentType.IsNullOrBlank() ||
                   !request.ExpectStatusDescription.IsNullOrBlank();
        }

        private ICache GetCache(RestBase request)
        {
            return request.Cache ?? Cache;
        }

        private IEnumerable<HttpPostParameter> GetPostParameters(RestBase request)
        {
            if(request.PostParameters != null)
            {
                foreach(var parameter in request.PostParameters)
                {
                    yield return parameter;
                }
            }

            if (PostParameters == null)
            {
                yield break;
            }

            foreach (var parameter in PostParameters)
            {
                yield return parameter;
            }
        }

        private CacheOptions GetCacheOptions(RestBase request)
        {
            return request.CacheOptions ?? CacheOptions;
        }

        private Func<string> GetCacheKeyFunction(RestBase request)
        {
            return request.CacheKeyFunction ?? CacheKeyFunction;
        }

        private string GetProxy(RestBase request)
        {
            return request.Proxy ?? Proxy;
        }

        private string GetUserAgent(RestBase request)
        {
            var userAgent = request.UserAgent.IsNullOrBlank()
                                ? UserAgent
                                : request.UserAgent;
            return userAgent;
        }

        private ISerializer GetSerializer(RestBase request)
        {
            return request.Serializer ?? Serializer;
        }

        private IWebCredentials GetWebCredentials(RestBase request)
        {
            var credentials = request.Credentials ?? Credentials;
            return credentials;
        }

        private IWebQueryInfo GetInfo(RestBase request)
        {
            var info = request.Info ?? Info;
            return info;
        }

        private TimeSpan? GetTimeout(RestBase request)
        {
            return request.Timeout ?? Timeout;
        }

        private WebMethod GetWebMethod(RestBase request)
        {
            var method = !request.Method.HasValue
                             ? !Method.HasValue
                                   ? WebMethod.Get
                                   : Method.Value
                             : request.Method.Value;

            return method;
        }

        private RetryPolicy GetRetryPolicy(RestBase request)
        {
            var policy = request.RetryPolicy ?? RetryPolicy;
            return policy;
        }
        
        private TaskOptions GetTaskOptions(RestBase request)
        {
            var options = request.TaskOptions ?? TaskOptions;
            return options;
        }

        public virtual IAsyncResult BeginRequest(RestRequest request, RestCallback callback)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */);
        }

        public virtual IAsyncResult BeginRequest<T>(RestRequest request, RestCallback<T> callback)
        {
            return BeginRequest(request, callback, null, null, false /* isInternal */);
        }

        public RestResponse EndRequest(IAsyncResult result)
        {
            throw new NotImplementedException();
        }

        public RestResponse<T> EndRequest<T>(IAsyncResult result)
        {
            throw new NotImplementedException();
        }

        private IAsyncResult BeginRequest(RestRequest request, 
                                          RestCallback callback,
                                          WebQuery query,
                                          string url,
                                          bool isInternal)
        {
            if (!isInternal)
            {
                // [DC]: Recursive call possible, only do this once
                var uri = request.BuildEndpoint(this);
                query = GetQueryFor(request, uri);
                url = uri.ToString();
            }
            
            if (RequestExpectsMock(request))
            {
                url = BuildMockRequestUrl(request, query, url);
            }

            var retryPolicy = GetRetryPolicy(request);
            _remainingRetries = (retryPolicy != null
                                     ? retryPolicy.RetryCount
                                     : 0);

            Func<WebQueryAsyncResult> beginRequest
                = () => BeginRequestFunction(isInternal, 
                        request, 
                        query, 
                        url, 
                        callback);

            var result = beginRequest.Invoke();

            WebQueryResult previous = null;
            query.QueryResponse += (sender, args) =>
                                       {
                                           query.Result.PreviousResult = previous;
                                           var current = query.Result;

                                           var retry = false;
                                           if (retryPolicy != null)
                                           {
                                               // [DC]: Query should already have exception applied
                                               var exception = query.Result.Exception;

                                               // Known error retries
                                               foreach (RetryErrorCondition condition in retryPolicy.RetryConditions)
                                               {
                                                   if (exception == null)
                                                   {
                                                       continue;
                                                   }
                                                   retry |= condition.RetryIf(exception);
                                               }

                                               // Generic unknown retries?
                                               // todo

                                               if (retry)
                                               {
                                                   previous = current;
                                                   BeginRequest(request, callback, query, url, true);
                                                   Interlocked.Decrement(ref _remainingRetries);
                                               }
                                               else
                                               {   
                                                   _remainingRetries = 0;
                                               }
                                           }
                                           else
                                           {
                                               _remainingRetries = 0;
                                           }

                                           query.Result = current;

                                           // [DC]: Callback is for a final result, not a retry
                                           if (_remainingRetries == 0)
                                           {
                                               var response = BuildResponseFromResult(request, query);
                                               result.IsCompleted = true;
                                               if (callback != null)
                                               {
                                                   callback.Invoke(request, response);
                                               }
                                               result.Signal();
                                           }
                                       };

            return result;
        }

        private WebQueryAsyncResult BeginRequestFunction(bool isInternal, RestRequest request, WebQuery query, string url, RestCallback callback)
        {
            if(!isInternal)
            {
                WebQueryAsyncResult result;
                if (BeginRequestWithTask(request, callback, query, url, out result))
                {
                    return result;
                }

                if (BeginRequestWithCache(request, query, url, out result))
                {
                    return result;
                }

                if (BeginRequestMultiPart(request, query, url, out result))
                {
                    return result;
                }
            }

            // Normal operation
            return query.RequestAsync(url);
        }

        // [DC]: Should look for further code sharing with non-generic
        private IAsyncResult BeginRequest<T>(RestRequest request,
                                             RestCallback<T> callback,
                                             WebQuery query,
                                             string url,
                                             bool isInternal)
        {
            if (!isInternal)
            {
                var uri = request.BuildEndpoint(this);
                query = GetQueryFor(request, uri);
                url = uri.ToString();
            }

            if (RequestExpectsMock(request))
            {
                url = BuildMockRequestUrl(request, query, url);
            }

            var retryPolicy = GetRetryPolicy(request);
            _remainingRetries = (retryPolicy != null
                                     ? retryPolicy.RetryCount
                                     : 0);

            Func<WebQueryAsyncResult> beginRequest
                = () => BeginRequestFunction(
                    isInternal, 
                    request, 
                    query, 
                    url, 
                    callback);

            var result = beginRequest.Invoke();

            WebQueryResult previous = null;
            query.QueryResponse += (sender, args) =>
            {
                query.Result.PreviousResult = previous;
                var current = query.Result;

                var retry = false;
                if (retryPolicy != null)
                {
                    // [DC]: Query should already have exception applied
                    var exception = query.Result.Exception;
                    foreach (RetryErrorCondition condition in retryPolicy.RetryConditions)
                    {
                        if (exception == null)
                        {
                            continue;
                        }
                        retry |= condition.RetryIf(exception);
                    }

                    if (retry)
                    {
                        previous = current;
                        BeginRequest(request, callback, query, url, true);
                        Interlocked.Decrement(ref _remainingRetries);
                    }
                    else
                    {
                        _remainingRetries = 0;
                    }
                }
                else
                {
                    _remainingRetries = 0;
                }

                query.Result = current;

                // [DC]: Callback is for a final result, not a retry
                if (_remainingRetries == 0)
                {
                    var response = BuildResponseFromResult<T>(request, query);
                    result.IsCompleted = true;
                    if(callback != null)
                    {
                        callback.Invoke(request, response);
                    }
                    result.Signal();
                }
            };

            return result;
        }

        private WebQueryAsyncResult BeginRequestFunction<T>(bool isInternal, RestRequest request, WebQuery query, string url, RestCallback<T> callback)
        {
            if (!isInternal)
            {
                WebQueryAsyncResult result;
                if (BeginRequestWithTask(request, callback, query, url, out result))
                {
                    return result;
                }

                if (BeginRequestWithCache(request, query, url, out result))
                {
                    return result;
                }

                if (BeginRequestMultiPart(request, query, url, out result))
                {
                    return result;
                }
            }

            // Normal operation
            return query.RequestAsync(url);
        }

        private bool BeginRequestWithTask(RestRequest request,
                                          RestCallback callback,
                                          WebQuery query,
                                          string url,
                                          out WebQueryAsyncResult result)
        {
            var taskOptions = GetTaskOptions(request);
            if (taskOptions == null)
            {
                result = null;
                return false;
            }

            if (taskOptions.RepeatInterval <= TimeSpan.Zero)
            {
                result = null;
                return false;
            }

#if !NETCF
            if (!taskOptions.GetType().IsGenericType)
            {
#endif
                // Tasks without rate limiting
                _task = new TimedTask(taskOptions.DueTime,
                                      taskOptions.RepeatInterval,
                                      taskOptions.RepeatTimes,
                                      taskOptions.ContinueOnError,
                                      skip => BeginRequest(request,
                                                           callback,
                                                           query,
                                                           url,
                                                           true));
#if !NETCF
            }
            else
            {
                // Tasks with rate limiting
                var task = BuildRateLimitingTask(request,
                                                 taskOptions,
                                                 callback,
                                                 query,
                                                 url);

                _task = (TimedTask) task;
            }
#endif
            var action = new Action(
                () => _task.Start()
                );

            var inner = action.BeginInvoke(ar =>
                                            {
                                                /* No callback */
                                            }, null);
            result = new WebQueryAsyncResult { InnerResult = inner };
            return true;
        }

        private bool BeginRequestWithTask<T>(RestRequest request,
                                          RestCallback<T> callback,
                                          WebQuery query,
                                          string url,
                                          out WebQueryAsyncResult result)
        {
            var taskOptions = GetTaskOptions(request);
            if (taskOptions == null)
            {
                result = null;
                return false;
            }

            if (taskOptions.RepeatInterval <= TimeSpan.Zero)
            {
                result = null;
                return false;
            }

#if !NETCF
            if (!taskOptions.GetType().IsGenericType)
            {
#endif
                // Tasks without rate limiting
                _task = new TimedTask(taskOptions.DueTime,
                                      taskOptions.RepeatInterval,
                                      taskOptions.RepeatTimes,
                                      taskOptions.ContinueOnError,
                                      skip => BeginRequest(request,
                                                           callback,
                                                           query,
                                                           url,
                                                           true));
#if !NETCF
            }
            else
            {
                // Tasks with rate limiting
                var task = BuildRateLimitingTask(request,
                                                 taskOptions,
                                                 callback,
                                                 query,
                                                 url);

                _task = (TimedTask) task;
            }
#endif
            var action = new Action(
                () => _task.Start()
                );

            var inner = action.BeginInvoke(ar =>
                                               {
                                                   /* No callback */
                                               }, null);
            result = new WebQueryAsyncResult {InnerResult = inner};
            return true;
        }

#if !NETCF
        private object BuildRateLimitingTask(RestRequest request,
                                            ITaskOptions taskOptions,
                                            RestCallback callback,
                                            WebQuery query,
                                            string url)
        {
            var taskAction = new Action<bool>(skip => BeginRequest(request, callback, query, url, true));

            return BuildRateLimitingTaskImpl(taskOptions, taskAction);
        }

        private object BuildRateLimitingTask<T>(RestRequest request,
                                            ITaskOptions taskOptions,
                                            RestCallback<T> callback,
                                            WebQuery query,
                                            string url)
        {
            var taskAction = new Action<bool>(skip => BeginRequest(request, 
                                                                   callback, 
                                                                   query, 
                                                                   url, 
                                                                   true));

            return BuildRateLimitingTaskImpl(taskOptions, taskAction);
        }

        private static object BuildRateLimitingTaskImpl(ITaskOptions taskOptions, 
                                                        Action<bool> taskAction)
        {
            var innerType = taskOptions.GetDeclaredTypeForGeneric(typeof(ITaskOptions<>));
            var rateType = typeof(RateLimitingRule<>).MakeGenericType(innerType);
            var taskType = typeof(TimedTask<>).MakeGenericType(innerType);
            var rateLimitingType = (RateLimitType)taskOptions.GetValue("RateLimitType");
                
            object taskRule;
            switch(rateLimitingType)
            {
                case RateLimitType.ByPercent:
                    var rateLimitingPercent = taskOptions.GetValue("RateLimitPercent");
                    taskRule = Activator.CreateInstance(
                        rateType, rateLimitingPercent
                        );
                    break;
                case RateLimitType.ByPredicate:
                    var rateLimitingPredicate = taskOptions.GetValue("RateLimitIf");
                    taskRule = Activator.CreateInstance(
                        rateType, rateLimitingPredicate
                        );
                    var getRateLimitStatus = taskOptions.GetValue("GetRateLimitStatus");
                    if (getRateLimitStatus != null)
                    {
                        rateType.SetValue("GetRateLimitStatus", getRateLimitStatus);
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            return Activator.CreateInstance(taskType,
                                            taskOptions.DueTime, 
                                            taskOptions.RepeatInterval, 
                                            taskOptions.RepeatTimes,
                                            taskOptions.ContinueOnError,
                                            taskAction,
                                            taskRule);
        }
#endif

        private bool BeginRequestMultiPart(RestBase request,
                                           WebQuery query, 
                                           string url, 
                                           out WebQueryAsyncResult result)
        {
            var parameters = GetPostParameters(request);
            if (parameters == null || parameters.Count() == 0)
            {
                result = null;
                return false;
            }

            // [DC]: Default to POST if no method provided
            query.Method = query.Method != WebMethod.Post && Method != WebMethod.Put ? WebMethod.Post : query.Method;
            result = query.RequestAsync(url, parameters);
            return true;
        }

        private bool BeginRequestWithCache(RestBase request,
                                           WebQuery query, 
                                           string url, 
                                           out WebQueryAsyncResult result)
        {
            var cache = GetCache(request);
            if (Cache == null)
            {
                result = null;
                return false;
            }

            var options = GetCacheOptions(request);
            if (options == null)
            {
                result = null;
                return false;
            }

            // [DC]: This is currently prefixed to the full URL
            var function = GetCacheKeyFunction(request);
            var key = function != null ? function.Invoke() : "";
            
            switch (options.Mode)
            {
                case CacheMode.NoExpiration:
                    result = query.RequestAsync(url, key, cache);
                    break;
                case CacheMode.AbsoluteExpiration:
                    var expiry = options.Duration.FromNow();
                    result = query.RequestAsync(url, key, cache, expiry);
                    break;
                case CacheMode.SlidingExpiration:
                    result = query.RequestAsync(url, key, cache, options.Duration);
                    break;
                default:
                    throw new NotSupportedException("Unknown CacheMode");
            }

            return true;
        }
        
        private RestResponse BuildResponseFromResult(RestRequest request, WebQuery query)
        {
            var result = query.Result;
            var response = BuildBaseResponse(result);

            DeserializeEntityBody(result, request, response);

            return response;
        }

        private RestResponse<T> BuildResponseFromResult<T>(RestBase request, WebQuery query)
        {
            var result = query.Result;
            var response = BuildBaseResponse<T>(result);

            DeserializeEntityBody(result, request, response);

            return response;
        }

        private static RestResponse BuildBaseResponse(WebQueryResult result)
        {
            var response = new RestResponse
                       {
                           StatusCode = (HttpStatusCode)result.ResponseHttpStatusCode,
                           StatusDescription = result.ResponseHttpStatusDescription,
                           Content = result.Response,
                           ContentType = result.ResponseType,
                           ContentLength = result.ResponseLength,
                           ResponseUri = result.ResponseUri,
                       };

            return response;
        }

        private static RestResponse<T> BuildBaseResponse<T>(WebQueryResult result)
        {
            var response = new RestResponse<T>
            {
                StatusCode = (HttpStatusCode)result.ResponseHttpStatusCode,
                StatusDescription = result.ResponseHttpStatusDescription,
                Content = result.Response,
                ContentType = result.ResponseType,
                ContentLength = result.ResponseLength,
                ResponseUri = result.ResponseUri,
            };

            return response;
        }

        private void DeserializeEntityBody(WebQueryResult result, RestRequest request, RestResponse response)
        {
            var deserializer = request.Deserializer ?? Deserializer;
            if(deserializer != null && !result.Response.IsNullOrBlank() && request.ResponseEntityType != null)
            {
                response.ContentEntity = deserializer.Deserialize(result.Response, request.ResponseEntityType);
            }
        }

        private void DeserializeEntityBody<T>(WebQueryResult result, RestBase request, RestResponse<T> response)
        {
            var deserializer = request.Deserializer ?? Deserializer;
            if (deserializer != null && !result.Response.IsNullOrBlank())
            {
                response.ContentEntity = deserializer.Deserialize<T>(result.Response);
            }
        }

        private void SetQueryMeta(RestRequest request, WebQuery query)
        {
            // mocks

            // [DC]: Trump duplicates by request over client value
            query.Parameters.AddRange(Parameters);
            foreach(var parameter in request.Parameters)
            {
                if(query.Parameters[parameter.Name] != null)
                {
                    query.Parameters[parameter.Name].Value = parameter.Value;
                }
                else
                {
                    query.Parameters.Add(parameter);
                }
            }
            query.Headers.AddRange(Headers);
            query.Headers.AddRange(request.Headers);

            // [DC]: These properties are trumped by request over client
            query.UserAgent = GetUserAgent(request);
            query.Method = GetWebMethod(request);
            query.Proxy = GetProxy(request);
            query.RequestTimeout = GetTimeout(request);
            
            SerializeEntityBody(query, request);
        }

        private void SerializeEntityBody(WebQuery query, RestRequest request)
        {
            var serializer = GetSerializer(request);
            if (serializer == null)
            {
                // No suitable serializer for entity
                return;
            }

            var entityBody = serializer.Serialize(request.Entity, request.RequestEntityType);
            query.Entity = !entityBody.IsNullOrBlank()
                               ? new WebEntity
                                     {
                                         Content = entityBody,
                                         ContentEncoding = serializer.ContentEncoding,
                                         ContentType = serializer.ContentType
                                     }
                               : null;
        }

        private WebEntity SerializeExpectEntity(RestRequest request)
        {
            var serializer = GetSerializer(request);
            if (serializer == null || request.ExpectEntity == null)
            {
                // No suitable serializer or entity
                return null;
            }

            var entityBody = serializer.Serialize(request.ExpectEntity, request.RequestEntityType);
            var entity = !entityBody.IsNullOrBlank()
                               ? new WebEntity
                               {
                                   Content = entityBody,
                                   ContentEncoding = serializer.ContentEncoding,
                                   ContentType = serializer.ContentType
                               } : null;
            return entity;
        }

        private WebQuery GetQueryFor(RestBase request, Uri uri)
        {
            var method = GetWebMethod(request);
            var credentials = GetWebCredentials(request);
            var info = GetInfo(request);
            
            // [DC]: UserAgent is set via Info
            // [DC]: Request credentials trump client credentials
            var query = credentials != null
                            ? credentials.GetQueryFor(uri.ToString(), request, info, method)
                            : new BasicAuthWebQuery(info);

            return query;
        }
    }
}