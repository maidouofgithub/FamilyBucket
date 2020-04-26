﻿using Bucket.Rpc.Exceptions;
using Bucket.Rpc.Messages;
using Bucket.Rpc.Server;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Bucket.Rpc.Transport.Implementation
{
    /// <summary>
    /// 一个默认的传输客户端实现。
    /// </summary>
    public class TransportClient : ITransportClient, IDisposable
    {
        #region Field

        private readonly IMessageSender _messageSender;
        private readonly IMessageListener _messageListener;
        private readonly ILogger _logger;
        private readonly IServiceExecutor _serviceExecutor;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<TransportMessage>> _resultDictionary =
            new ConcurrentDictionary<string, TaskCompletionSource<TransportMessage>>();

        #endregion Field

        #region Constructor

        public TransportClient(IMessageSender messageSender, IMessageListener messageListener, ILogger logger,
            IServiceExecutor serviceExecutor)
        {
            _messageSender = messageSender;
            _messageListener = messageListener;
            _logger = logger;
            _serviceExecutor = serviceExecutor;
            _messageListener.Received += MessageListener_Received;
        }

        #endregion Constructor

        #region Implementation of ITransportClient

        /// <summary>
        /// 发送消息。
        /// </summary>
        /// <param name="message">远程调用消息模型。</param>
        /// <returns>远程调用消息的传输消息。</returns>
        public async Task<RemoteInvokeResultMessage> SendAsync(RemoteInvokeMessage message, int timeout)
        {
            try
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("准备发送消息。");

                var transportMessage = TransportMessage.CreateInvokeMessage(message);

                //注册结果回调
                var callbackTask = RegisterResultCallbackAsync(transportMessage.Id, timeout);

                try
                {
                    //发送
                    await _messageSender.SendAndFlushAsync(transportMessage);
                }
                catch (Exception exception)
                {
                    throw new RpcCommunicationException("与服务端通讯时发生了异常。", exception);
                }

                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("消息发送成功。");

                return await callbackTask;
            }
            catch (Exception exception)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                    _logger.LogError(exception, "消息发送失败。");
                throw exception;
            }
        }

        #endregion Implementation of ITransportClient

        #region Implementation of IDisposable

        /// <summary>Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.</summary>
        public void Dispose()
        {
            (_messageSender as IDisposable)?.Dispose();
            (_messageListener as IDisposable)?.Dispose();

            foreach (var taskCompletionSource in _resultDictionary.Values)
            {
                taskCompletionSource.TrySetCanceled();
            }
        }

        #endregion Implementation of IDisposable

        #region Private Method

        /// <summary>
        /// 注册指定消息的回调任务。
        /// </summary>
        /// <param name="id">消息Id。</param>
        /// <param name="timeout">请求超时时间，单位秒</param>
        /// <returns>远程调用结果消息模型。</returns>
        private async Task<RemoteInvokeResultMessage> RegisterResultCallbackAsync(string id, int timeout)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug($"准备获取Id为：{id}的响应内容。");

            var source = new TaskCompletionSource<TransportMessage>();
            var tokenSource = new CancellationTokenSource();
            tokenSource.Token.Register(() =>
            {
                _resultDictionary.TryRemove(id, out TaskCompletionSource<TransportMessage> value);
                if (!source.Task.IsCompleted)
                {
                    source.TrySetException(new RpcRemoteException(408, $"获取Id为{id}的响应超时"));
                }
            });
            _resultDictionary.TryAdd(id, source);
            try
            {
                tokenSource.CancelAfter(timeout * 1000);
                var result = await source.Task;
                return result.GetContent<RemoteInvokeResultMessage>();
            }
            finally
            {
                //删除回调任务
                _resultDictionary.TryRemove(id, out TaskCompletionSource<TransportMessage> value);
            }
        }

        private async Task MessageListener_Received(IMessageSender sender, TransportMessage message)
        {
            if (_logger.IsEnabled(LogLevel.Information))
                _logger.LogInformation($"接收到{message.Id}消息。");

            if (!_resultDictionary.TryGetValue(message.Id, out TaskCompletionSource<TransportMessage> source))
                return;

            if (message.IsInvokeResultMessage())
            {
                var content = message.GetContent<RemoteInvokeResultMessage>();
                if (!string.IsNullOrEmpty(content.ExceptionMessage))
                {
                    source.TrySetException(new RpcRemoteException(content.StatusCode, content.ExceptionMessage));
                }
                else
                {
                    source.SetResult(message);
                }
            }
            if (_serviceExecutor != null && message.IsInvokeMessage())
                await _serviceExecutor.ExecuteAsync(sender, message);
        }

        #endregion Private Method
    }
}
