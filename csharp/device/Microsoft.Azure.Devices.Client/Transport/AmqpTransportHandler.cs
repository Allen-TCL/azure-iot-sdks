﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.Azure.Devices.Client.Transport
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.Amqp;
    using Microsoft.Azure.Amqp.Framing;
    using Microsoft.Azure.Devices.Client.Exceptions;
    using Microsoft.Azure.Devices.Client.Extensions;

    sealed class AmqpTransportHandler : TransportHandler
    {
        static readonly IotHubConnectionCache TcpConnectionCache = new IotHubConnectionCache();
        static readonly IotHubConnectionCache WsConnectionCache = new IotHubConnectionCache();
        readonly string deviceId;
        readonly Client.FaultTolerantAmqpObject<SendingAmqpLink> faultTolerantEventSendingLink;
        readonly Client.FaultTolerantAmqpObject<ReceivingAmqpLink> faultTolerantDeviceBoundReceivingLink;
        readonly IotHubConnectionString iotHubConnectionString;
        readonly TimeSpan openTimeout;
        readonly TimeSpan operationTimeout;
        readonly uint prefetchCount;

        int eventsDeliveryTag;
        int closed;

        internal AmqpTransportHandler(IPipelineContext context, IotHubConnectionString connectionString, AmqpTransportSettings transportSettings)
            :base(context, transportSettings)
        {
            TransportType transportType = transportSettings.GetTransportType();
            this.deviceId = connectionString.DeviceId;
            switch (transportType)
            {
                case TransportType.Amqp_Tcp_Only:
                    this.IotHubConnection = TcpConnectionCache.GetConnection(connectionString, transportSettings);
                    break;
                case TransportType.Amqp_WebSocket_Only:
                    this.IotHubConnection = WsConnectionCache.GetConnection(connectionString, transportSettings);
                    break;
                default:
                    throw new InvalidOperationException("Invalid Transport Type {0}".FormatInvariant(transportType));
            }

            this.openTimeout = transportSettings.OpenTimeout;
            this.operationTimeout = transportSettings.OperationTimeout;
            this.prefetchCount = transportSettings.PrefetchCount;
            this.faultTolerantEventSendingLink = new Client.FaultTolerantAmqpObject<SendingAmqpLink>(this.CreateEventSendingLinkAsync, this.IotHubConnection.CloseLink);
            this.faultTolerantDeviceBoundReceivingLink = new Client.FaultTolerantAmqpObject<ReceivingAmqpLink>(this.CreateDeviceBoundReceivingLinkAsync, this.IotHubConnection.CloseLink);
            this.iotHubConnectionString = connectionString;
        }

        internal IotHubConnection IotHubConnection { get; }

        public override async Task OpenAsync(bool explicitOpen, CancellationToken cancellationToken)
        {
            if (!explicitOpen)
            {
                return;
            }

            await this.HandleTimeoutCancellation(async () =>
             {
                 try
                 {
                     await Task.WhenAll(
                         this.faultTolerantEventSendingLink.OpenAsync(this.openTimeout, cancellationToken),
                         this.faultTolerantDeviceBoundReceivingLink.OpenAsync(this.openTimeout, cancellationToken));
                 }
                 catch (Exception exception)
                 {
                     if (exception.IsFatal())
                     {
                         throw;
                     }

                     throw AmqpClientHelper.ToIotHubClientContract(exception);
                 }
             }, cancellationToken);
        }

        public override Task CloseAsync()
        {
            this.Close();
            return TaskHelpers.CompletedTask;
        }

        public override async Task SendEventAsync(Message message, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                Outcome outcome;
                using (AmqpMessage amqpMessage = message.ToAmqpMessage())
                {
                    outcome = await this.SendAmqpMessageAsync(amqpMessage, cancellationToken);
                }

                if (outcome.DescriptorCode != Accepted.Code)
                {
                    throw AmqpErrorMapper.GetExceptionFromOutcome(outcome);
                }
            }, cancellationToken);
        }

        public override async Task SendEventAsync(IEnumerable<Message> messages, CancellationToken cancellationToken)
        {
            await this.HandleTimeoutCancellation(async () =>
            {
                // List to hold messages in Amqp friendly format
                var messageList = new List<Data>();

                foreach (Message message in messages)
                {
                    using (AmqpMessage amqpMessage = message.ToAmqpMessage())
                    {
                        var data = new Data()
                        {
                            Value = MessageConverter.ReadStream(amqpMessage.ToStream())
                        };
                        messageList.Add(data);
                    }
                }

                Outcome outcome;
                using (AmqpMessage amqpMessage = AmqpMessage.Create(messageList))
                {
                    amqpMessage.MessageFormat = AmqpConstants.AmqpBatchedMessageFormat;
                    outcome = await this.SendAmqpMessageAsync(amqpMessage, cancellationToken);
                }

                if (outcome.DescriptorCode != Accepted.Code)
                {
                    throw AmqpErrorMapper.GetExceptionFromOutcome(outcome);
                }
            }, cancellationToken);
        }

        public override async Task<Message> ReceiveAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            Message message = null;

            await this.HandleTimeoutCancellation(async () =>
            {
                AmqpMessage amqpMessage;
                try
                {
                    ReceivingAmqpLink deviceBoundReceivingLink = await this.GetDeviceBoundReceivingLinkAsync(cancellationToken);
                    amqpMessage = await deviceBoundReceivingLink.ReceiveMessageAsync(timeout);
                }
                catch (Exception exception)
                {
                    if (exception.IsFatal())
                    {
                        throw;
                    }

                    throw AmqpClientHelper.ToIotHubClientContract(exception);
                }

                if (amqpMessage != null)
                {
                    message = new Message(amqpMessage)
                    {
                        LockToken = new Guid(amqpMessage.DeliveryTag.Array).ToString()
                    };
                }
                else
                {
                    message = null;
                }
            }, cancellationToken);

            return message;
        }

        public override Task CompleteAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.AcceptedOutcome, cancellationToken), cancellationToken);
        }

        public override Task AbandonAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.ReleasedOutcome, cancellationToken), cancellationToken);
        }

        public override Task RejectAsync(string lockToken, CancellationToken cancellationToken)
        {
            return this.HandleTimeoutCancellation(() => this.DisposeMessageAsync(lockToken, AmqpConstants.RejectedOutcome, cancellationToken), cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                this.Close();
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        void Close()
        {
            if (Interlocked.CompareExchange(ref this.closed, 1, 0) == 0)
            {
                GC.SuppressFinalize(this);
                this.faultTolerantEventSendingLink.CloseAsync().Fork();
                this.faultTolerantDeviceBoundReceivingLink.CloseAsync().Fork();
                this.IotHubConnection.Release(this.deviceId);
            }
        }

        async Task<Outcome> SendAmqpMessageAsync(AmqpMessage amqpMessage, CancellationToken cancellationToken)
        {
            Outcome outcome;
            try
            {
                SendingAmqpLink eventSendingLink = await this.GetEventSendingLinkAsync(cancellationToken);
                outcome = await eventSendingLink.SendMessageAsync(amqpMessage, IotHubConnection.GetNextDeliveryTag(ref this.eventsDeliveryTag), AmqpConstants.NullBinary, this.operationTimeout);
            }
            catch (Exception exception)
            {
                if (exception.IsFatal())
                {
                    throw;
                }

                throw AmqpClientHelper.ToIotHubClientContract(exception);
            }

            return outcome;
        }

        async Task DisposeMessageAsync(string lockToken, Outcome outcome, CancellationToken cancellationToken)
        {
            ArraySegment<byte> deliveryTag = IotHubConnection.ConvertToDeliveryTag(lockToken);

            Outcome disposeOutcome;
            try
            {
                ReceivingAmqpLink deviceBoundReceivingLink = await this.GetDeviceBoundReceivingLinkAsync(cancellationToken);
                disposeOutcome = await deviceBoundReceivingLink.DisposeMessageAsync(deliveryTag, outcome, batchable: true, timeout: this.operationTimeout);
            }
            catch (Exception exception)
            {
                if (exception.IsFatal())
                {
                    throw;
                }

                throw AmqpClientHelper.ToIotHubClientContract(exception);
            }

            if (disposeOutcome.DescriptorCode != Accepted.Code)
            {
                if (disposeOutcome.DescriptorCode == Rejected.Code)
                {
                    var rejected = (Rejected)disposeOutcome;

                    // Special treatment for NotFound amqp rejected error code in case of DisposeMessage 
                    if (rejected.Error != null && rejected.Error.Condition.Equals(AmqpErrorCode.NotFound))
                    {
                        throw new DeviceMessageLockLostException(rejected.Error.Description);
                    }
                }

                throw AmqpErrorMapper.GetExceptionFromOutcome(disposeOutcome);
            }
        }

        async Task<SendingAmqpLink> GetEventSendingLinkAsync(CancellationToken cancellationToken)
        {
            SendingAmqpLink eventSendingLink;
            if (!this.faultTolerantEventSendingLink.TryGetOpenedObject(out eventSendingLink))
            {
                eventSendingLink = await this.faultTolerantEventSendingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }
            return eventSendingLink;
        }

        async Task<SendingAmqpLink> CreateEventSendingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceEventPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            return await this.IotHubConnection.CreateSendingLinkAsync(path, this.iotHubConnectionString, timeout, cancellationToken);
        }

        async Task<ReceivingAmqpLink> GetDeviceBoundReceivingLinkAsync(CancellationToken cancellationToken)
        {
            ReceivingAmqpLink deviceBoundReceivingLink;
            if (!this.faultTolerantDeviceBoundReceivingLink.TryGetOpenedObject(out deviceBoundReceivingLink))
            {
                deviceBoundReceivingLink = await this.faultTolerantDeviceBoundReceivingLink.GetOrCreateAsync(this.openTimeout, cancellationToken);
            }

            return deviceBoundReceivingLink;
        }

        async Task<ReceivingAmqpLink> CreateDeviceBoundReceivingLinkAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            string path = string.Format(CultureInfo.InvariantCulture, CommonConstants.DeviceBoundPathTemplate, System.Net.WebUtility.UrlEncode(this.deviceId));

            return await this.IotHubConnection.CreateReceivingLinkAsync(path, this.iotHubConnectionString, timeout, this.prefetchCount, cancellationToken);
        }
    }
}
