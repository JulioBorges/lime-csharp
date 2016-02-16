using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Lime.Messaging.Resources;
using NUnit.Framework;
using System.Threading.Tasks;
using Lime.Protocol.Client;
using Lime.Protocol.Network;
using Moq;
using System.Threading;
using Lime.Protocol.Security;
using Shouldly;
using Lime.Protocol.Util;

namespace Lime.Protocol.UnitTests.Client
{
    [TestFixture]
    public class OnDemandClientChannelTests
    {
        private TimeSpan _sendTimeout;
        private CancellationToken _cancellationToken;
        private Guid _sessionId;
        private Mock<IClientChannelBuilder> _clientChannelBuilder;
        private Mock<IEstablishedClientChannelBuilder> _establishedClientChannelBuilder;
        private Mock<IClientChannel> _clientChannel;
        private Mock<IDisposable> _disposableClientChannel;
        private Mock<ITransport> _transport;

        [SetUp]
        public void Setup()
        {
            _sendTimeout = TimeSpan.FromSeconds(5);
            _cancellationToken = _sendTimeout.ToCancellationToken();
            _sessionId = Guid.NewGuid();            
            _transport = new Mock<ITransport>();
            _transport
                .SetupGet(t => t.IsConnected)
                .Returns(true);
            _clientChannel = new Mock<IClientChannel>();
            _clientChannel
                .SetupGet(c => c.SessionId)
                .Returns(_sessionId);
            _clientChannel
                .SetupGet(c => c.Transport)
                .Returns(_transport.Object);
            _clientChannel
                .SetupGet(c => c.State)
                .Returns(SessionState.Established);
            _disposableClientChannel = _clientChannel.As<IDisposable>();
            _clientChannelBuilder = new Mock<IClientChannelBuilder>();
            _establishedClientChannelBuilder = new Mock<IEstablishedClientChannelBuilder>();
            _establishedClientChannelBuilder
                .Setup(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(_clientChannel.Object);
            _establishedClientChannelBuilder
                .SetupGet(b => b.ChannelBuilder)
                .Returns(_clientChannelBuilder.Object);
            _clientChannelBuilder
                .SetupGet(b => b.SendTimeout)
                .Returns(_sendTimeout);
        }

        [TearDown]
        public void Teardown()
        {
            _clientChannel = null;
            _transport = null;
            _establishedClientChannelBuilder = null;
            _clientChannelBuilder = null;
        }

        private OnDemandClientChannel GetTarget()
        {
            return new OnDemandClientChannel(_establishedClientChannelBuilder.Object);
        }

        [Test]
        public async Task SendMessageAsync_NotEstablishedChannel_BuildChannelAndSends()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            ChannelInformation channelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {
                channelInformation = c;
                return TaskUtil.CompletedTask;
            });

            // Act
            await target.SendMessageAsync(message);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());
            channelInformation.ShouldNotBeNull();
            channelInformation.Id.ShouldBe(_sessionId);
            channelInformation.State.ShouldBe(SessionState.Established);
        }

        [Test]
        public async Task SendMessageAsync_NotEstablishedChannelMultipleCalls_BuildChannelOnceAndSends()
        {
            // Arrange
            var count = Dummy.CreateRandomInt(500) + 1;;
            var messages = new Message[count];
            for (int i = 0; i < count; i++)
            {
                messages[i] = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            }
            
            var target = GetTarget();

            // Act
            await Task.WhenAll(
                Enumerable
                    .Range(0, count)
                    .Select(i => Task.Run(() => target.SendMessageAsync(messages[i]))));


            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            foreach (var message in messages)
            {
                _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());
            }            
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendMessageAsync_ChannelCreatedHandlerThrowsException_ThrowsExceptionToTheCaller()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                throw exception;
            });

            // Act
            await target.SendMessageAsync(message);
        }

        [Test]
        public async Task SendMessageAsync_MultipleChannelCreatedHandlerThrowsException_ThrowsAggregateExceptionToTheCaller()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception1 = Dummy.CreateException<ApplicationException>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                throw exception1;
            });
            var exception2 = Dummy.CreateException<ApplicationException>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                throw exception2;
            });

            // Act
            try
            {
                await target.SendMessageAsync(message);
            }
            catch (AggregateException ex)
            {
                ex.InnerExceptions.Count.ShouldBe(2);
                ex.InnerExceptions.ShouldContain(exception1);
                ex.InnerExceptions.ShouldContain(exception2);
            }
        }

        [Test]
        public async Task SendMessageAsync_EstablishedChannel_SendsToExistingChannel()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            await target.SendMessageAsync(message);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            await target.SendMessageAsync(message);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());
        }

        [Test]
        public async Task SendMessageAsync_ChannelCreationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add((f) =>
            {                
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            ChannelInformation createdChannelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {                
                createdChannelInformation = c;
                return TaskUtil.CompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendMessageAsync(message);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());            
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();            
            createdChannelInformation.ShouldNotBeNull();
            createdChannelInformation.Id.ShouldBe(_sessionId);
        }

        [Test]
        public async Task SendMessageAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndSend()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();
            var handlerArgs = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add((f) =>
            {                
                handlerArgs.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendMessageAsync(message);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());
            handlerArgs.Count.ShouldBe(3);
            handlerArgs.Any(h => h.IsConnected).ShouldBeFalse();
            handlerArgs.Select(e => e.Exception).ShouldContain(exception1);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception2);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task SendMessageAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            target.Dispose();

            // Act
            await target.SendMessageAsync(message);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendMessageAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add((f) => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendMessageAsync(message);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendMessageAsync_ChannelCreationFailedMultipleHandlersOneReturnsFalse_ThrowsException()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();

            var handlerCallCount = 0;

            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                handlerCallCount++;
                return TaskUtil.TrueCompletedTask;
            });
            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                handlerCallCount++;
                return TaskUtil.FalseCompletedTask;
            });
            target.ChannelCreationFailedHandlers.Add((f) => 
            {
                handlerCallCount++;
                return TaskUtil.TrueCompletedTask;
            });
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            try
            {
                await target.SendMessageAsync(message);
            }
            catch
            {
                handlerCallCount.ShouldBe(3);
                throw;
            }
        }

        [Test]
        public async Task SendMessageAsync_ChannelOperationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var sessionId = Guid.NewGuid();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add((f) =>
            {                
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            var createdChannelInformations = new List<ChannelInformation>();
            target.ChannelCreatedHandlers.Add((c) =>
            {                
                createdChannelInformations.Add(c);
                return TaskUtil.CompletedTask;
            });

            ChannelInformation discardedChannelInformation = null;
            target.ChannelDiscardedHandlers.Add((c) =>
            {                                    
                discardedChannelInformation = c;
                return TaskUtil.CompletedTask;
            });
            _clientChannel
                .Setup(c => c.SendMessageAsync(message))
                .Throws(exception);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));
            clientChannel2
                .SetupGet(c => c.SessionId)
                .Returns(sessionId);
            clientChannel2
                .SetupGet(c => c.Transport)
                .Returns(_transport.Object);
            clientChannel2
                .SetupGet(c => c.State)
                .Returns(SessionState.Established);

            // Act
            await target.SendMessageAsync(message);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendMessageAsync(message), Times.Once());
            clientChannel2.Verify(c => c.SendMessageAsync(message), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            createdChannelInformations.Count.ShouldBe(2);
            createdChannelInformations[0].Id.ShouldBe(_sessionId);
            createdChannelInformations[1].Id.ShouldBe(sessionId);
            discardedChannelInformation.ShouldNotBeNull();
            discardedChannelInformation.Id.ShouldBe(_sessionId);
        }
                        
        [Test]
        public async Task ReceiveMessageAsync_NotEstablishedChannel_BuildChannelAndReceives()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(message);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());            
        }

        [Test]
        public async Task ReceiveMessageAsync_EstablishedChannel_ReceivesFromExistingChannel()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();
            await target.ReceiveMessageAsync(_cancellationToken);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(message);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());
        }

        [Test]
        public async Task ReceiveMessageAsync_ChannelCreationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add(f =>
            {    
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(message);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());            
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
        }

        [Test]
        public async Task ReceiveMessageAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndReceives()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();
            
            var failedChannelInformations = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add(f =>
            {                
                failedChannelInformations.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(message);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());
            failedChannelInformations.Count.ShouldBe(3);
            failedChannelInformations.Any(h => h.IsConnected).ShouldBeFalse();
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception1);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception2);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task ReceiveMessageAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange            
            var target = GetTarget();
            target.Dispose();

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);
        }

        [Test]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task ReceiveMessageAsync_CanceledToken_ThrowsTaskCanceledException()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var actual = await target.ReceiveMessageAsync(cts.Token);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task ReceiveMessageAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add(f => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);
        }

        [Test]
        public async Task ReceiveMessageAsync_ChannelOperationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var clientChannel2 = new Mock<IClientChannel>();
            
            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            _clientChannel
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .Throws(exception);
            clientChannel2
                .Setup(c => c.ReceiveMessageAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(message);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));

            // Act
            var actual = await target.ReceiveMessageAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(message);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
            clientChannel2.Verify(c => c.ReceiveMessageAsync(_cancellationToken), Times.Once());            
            failedChannelInformation.Exception.ShouldBe(exception);
        }

        [Test]
        public async Task SendNotificationAsync_NotEstablishedChannel_BuildChannelAndSends()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            ChannelInformation channelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {
                channelInformation = c;
                return TaskUtil.CompletedTask;
            });

            // Act
            await target.SendNotificationAsync(notification);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            channelInformation.ShouldNotBeNull();
            channelInformation.Id.ShouldBe(_sessionId);
            channelInformation.State.ShouldBe(SessionState.Established);
        }

        [Test]
        public async Task SendNotificationAsync_NotEstablishedChannelMultipleCalls_BuildChannelOnceAndSends()
        {
            // Arrange
            var count = Dummy.CreateRandomInt(500) + 1; ;
            var notifications = new Notification[count];
            for (int i = 0; i < count; i++)
            {
                notifications[i] = Dummy.CreateNotification(Event.Received);
            }

            var target = GetTarget();

            // Act
            await Task.WhenAll(
                Enumerable
                    .Range(0, count)
                    .Select(i => Task.Run(() => target.SendNotificationAsync(notifications[i]))));


            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            foreach (var notification in notifications)
            {
                _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            }
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendNotificationAsync_ChannelCreatedHandlerThrowsException_ThrowsExceptionToTheCaller()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                throw exception;
            });

            // Act
            await target.SendNotificationAsync(notification);
        }

        [Test]
        public async Task SendNotificationAsync_EstablishedChannel_SendsToExistingChannel()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            await target.SendNotificationAsync(notification);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            await target.SendNotificationAsync(notification);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
        }

        [Test]
        public async Task SendNotificationAsync_ChannelCreationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            ChannelInformation createdChannelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {
                createdChannelInformation = c;
                return TaskUtil.CompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendNotificationAsync(notification);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
            createdChannelInformation.ShouldNotBeNull();
            createdChannelInformation.Id.ShouldBe(_sessionId);
        }

        [Test]
        public async Task SendNotificationAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndSend()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();
            var handlerArgs = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                handlerArgs.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendNotificationAsync(notification);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            handlerArgs.Count.ShouldBe(3);
            handlerArgs.Any(h => h.IsConnected).ShouldBeFalse();
            handlerArgs.Select(e => e.Exception).ShouldContain(exception1);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception2);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task SendNotificationAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            target.Dispose();

            // Act
            await target.SendNotificationAsync(notification);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendNotificationAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add((f) => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendNotificationAsync(notification);
        }

        [Test]
        public async Task SendNotificationAsync_ChannelOperationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var sessionId = Guid.NewGuid();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add((f) =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            var createdChannelInformations = new List<ChannelInformation>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                createdChannelInformations.Add(c);
                return TaskUtil.CompletedTask;
            });

            ChannelInformation discardedChannelInformation = null;
            target.ChannelDiscardedHandlers.Add((c) =>
            {
                discardedChannelInformation = c;
                return TaskUtil.CompletedTask;
            });
            _clientChannel
                .Setup(c => c.SendNotificationAsync(notification))
                .Throws(exception);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));
            clientChannel2
                .SetupGet(c => c.SessionId)
                .Returns(sessionId);
            clientChannel2
                .SetupGet(c => c.Transport)
                .Returns(_transport.Object);
            clientChannel2
                .SetupGet(c => c.State)
                .Returns(SessionState.Established);

            // Act
            await target.SendNotificationAsync(notification);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            clientChannel2.Verify(c => c.SendNotificationAsync(notification), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            createdChannelInformations.Count.ShouldBe(2);
            createdChannelInformations[0].Id.ShouldBe(_sessionId);
            createdChannelInformations[1].Id.ShouldBe(sessionId);
            discardedChannelInformation.ShouldNotBeNull();
            discardedChannelInformation.Id.ShouldBe(_sessionId);
        }

        [Test]
        public async Task ReceiveNotificationAsync_NotEstablishedChannel_BuildChannelAndReceives()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(notification);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
        }

        [Test]
        public async Task ReceiveNotificationAsync_EstablishedChannel_ReceivesFromExistingChannel()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();
            await target.ReceiveNotificationAsync(_cancellationToken);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(notification);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
        }

        [Test]
        public async Task ReceiveNotificationAsync_ChannelCreationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(notification);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
        }

        [Test]
        public async Task ReceiveNotificationAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndReceives()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();

            var failedChannelInformations = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformations.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(notification);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
            failedChannelInformations.Count.ShouldBe(3);
            failedChannelInformations.Any(h => h.IsConnected).ShouldBeFalse();
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception1);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception2);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task ReceiveNotificationAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange            
            var target = GetTarget();
            target.Dispose();

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);
        }

        [Test]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task ReceiveNotificationAsync_CanceledToken_ThrowsTaskCanceledException()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var actual = await target.ReceiveNotificationAsync(cts.Token);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task ReceiveNotificationAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add(f => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);
        }

        [Test]
        public async Task ReceiveNotificationAsync_ChannelOperationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var notification = Dummy.CreateNotification(Event.Received);
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            _clientChannel
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .Throws(exception);
            clientChannel2
                .Setup(c => c.ReceiveNotificationAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(notification);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));

            // Act
            var actual = await target.ReceiveNotificationAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(notification);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
            clientChannel2.Verify(c => c.ReceiveNotificationAsync(_cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
        }
        
        [Test]
        public async Task SendCommandAsync_NotEstablishedChannel_BuildChannelAndSends()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            ChannelInformation channelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {
                channelInformation = c;
                return TaskUtil.CompletedTask;
            });

            // Act
            await target.SendCommandAsync(command);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
            channelInformation.ShouldNotBeNull();
            channelInformation.Id.ShouldBe(_sessionId);
            channelInformation.State.ShouldBe(SessionState.Established);
        }

        [Test]
        public async Task SendCommandAsync_NotEstablishedChannelMultipleCalls_BuildChannelOnceAndSends()
        {
            // Arrange
            var count = Dummy.CreateRandomInt(500) + 1; ;
            var commands = new Command[count];
            for (int i = 0; i < count; i++)
            {
                commands[i] = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            }

            var target = GetTarget();

            // Act
            await Task.WhenAll(
                Enumerable
                    .Range(0, count)
                    .Select(i => Task.Run(() => target.SendCommandAsync(commands[i]))));


            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            foreach (var command in commands)
            {
                _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
            }
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendCommandAsync_ChannelCreatedHandlerThrowsException_ThrowsExceptionToTheCaller()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                throw exception;
            });

            // Act
            await target.SendCommandAsync(command);
        }

        [Test]
        public async Task SendCommandAsync_EstablishedChannel_SendsToExistingChannel()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            await target.SendCommandAsync(command);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            await target.SendCommandAsync(command);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
        }

        [Test]
        public async Task SendCommandAsync_ChannelCreationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            ChannelInformation createdChannelInformation = null;
            target.ChannelCreatedHandlers.Add((c) =>
            {
                createdChannelInformation = c;
                return TaskUtil.CompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendCommandAsync(command);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
            createdChannelInformation.ShouldNotBeNull();
            createdChannelInformation.Id.ShouldBe(_sessionId);
        }

        [Test]
        public async Task SendCommandAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndSend()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();
            var handlerArgs = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add((f) =>
            {
                handlerArgs.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendCommandAsync(command);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
            handlerArgs.Count.ShouldBe(3);
            handlerArgs.Any(h => h.IsConnected).ShouldBeFalse();
            handlerArgs.Select(e => e.Exception).ShouldContain(exception1);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception2);
            handlerArgs.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task SendCommandAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            target.Dispose();

            // Act
            await target.SendCommandAsync(command);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task SendCommandAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add((f) => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            await target.SendCommandAsync(command);
        }

        [Test]
        public async Task SendCommandAsync_ChannelOperationFailed_RecreateChannelAndSend()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var sessionId = Guid.NewGuid();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add((f) =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            var createdChannelInformations = new List<ChannelInformation>();
            target.ChannelCreatedHandlers.Add((c) =>
            {
                createdChannelInformations.Add(c);
                return TaskUtil.CompletedTask;
            });

            ChannelInformation discardedChannelInformation = null;
            target.ChannelDiscardedHandlers.Add((c) =>
            {
                discardedChannelInformation = c;
                return TaskUtil.CompletedTask;
            });
            _clientChannel
                .Setup(c => c.SendCommandAsync(command))
                .Throws(exception);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));
            clientChannel2
                .SetupGet(c => c.SessionId)
                .Returns(sessionId);
            clientChannel2
                .SetupGet(c => c.Transport)
                .Returns(_transport.Object);
            clientChannel2
                .SetupGet(c => c.State)
                .Returns(SessionState.Established);

            // Act
            await target.SendCommandAsync(command);

            // Assert
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.SendCommandAsync(command), Times.Once());
            clientChannel2.Verify(c => c.SendCommandAsync(command), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            createdChannelInformations.Count.ShouldBe(2);
            createdChannelInformations[0].Id.ShouldBe(_sessionId);
            createdChannelInformations[1].Id.ShouldBe(sessionId);
            discardedChannelInformation.ShouldNotBeNull();
            discardedChannelInformation.Id.ShouldBe(_sessionId);
        }

        [Test]
        public async Task ReceiveCommandAsync_NotEstablishedChannel_BuildChannelAndReceives()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            var target = GetTarget();

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(command);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
        }

        [Test]
        public async Task ReceiveCommandAsync_EstablishedChannel_ReceivesFromExistingChannel()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            var target = GetTarget();
            await target.ReceiveCommandAsync(_cancellationToken);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(command);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
        }

        [Test]
        public async Task ReceiveCommandAsync_ChannelCreationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(command);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
        }

        [Test]
        public async Task ReceiveCommandAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndReceives()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();

            var failedChannelInformations = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformations.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(command);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
            failedChannelInformations.Count.ShouldBe(3);
            failedChannelInformations.Any(h => h.IsConnected).ShouldBeFalse();
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception1);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception2);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task ReceiveCommandAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange            
            var target = GetTarget();
            target.Dispose();

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);
        }

        [Test]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task ReceiveCommandAsync_CanceledToken_ThrowsTaskCanceledException()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            var target = GetTarget();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var actual = await target.ReceiveCommandAsync(cts.Token);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task ReceiveCommandAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add(f => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);
        }

        [Test]
        public async Task ReceiveCommandAsync_ChannelOperationFailed_RecreateChannelAndReceives()
        {
            // Arrange
            var command = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            _clientChannel
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
            clientChannel2
                .Setup(c => c.ReceiveCommandAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(command);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));

            // Act
            var actual = await target.ReceiveCommandAsync(_cancellationToken);

            // Assert
            actual.ShouldBe(command);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
            clientChannel2.Verify(c => c.ReceiveCommandAsync(_cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
        }

        [Test]
        public async Task ProcessCommandAsync_NotEstablishedChannel_BuildChannelAndProcesses()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            responseCommand.Id = requestCommand.Id;
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            var target = GetTarget();

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);

            // Assert
            actual.ShouldBe(responseCommand);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Once());
            _clientChannel.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
        }

        [Test]
        public async Task ProcessCommandAsync_EstablishedChannel_ProcessesWithExistingChannel()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            responseCommand.Id = requestCommand.Id;
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            var target = GetTarget();
            await target.ProcessCommandAsync(requestCommand, _cancellationToken);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);

            // Assert
            actual.ShouldBe(responseCommand);      
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Never());
            _clientChannel.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
        }

        [Test]
        public async Task ProcessCommandAsync_ChannelCreationFailed_RecreateChannelAndProcesses()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            responseCommand.Id = requestCommand.Id;
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            var target = GetTarget();
            var exception = Dummy.CreateException();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);

            // Assert
            actual.ShouldBe(responseCommand);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);
            failedChannelInformation.IsConnected.ShouldBeFalse();
        }

        [Test]
        public async Task ProcessCommandAsync_ChannelCreationFailsMultipleTimes_TryRecreateChannelAndProcesses()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            responseCommand.Id = requestCommand.Id;
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            var target = GetTarget();
            var exception1 = Dummy.CreateException();
            var exception2 = Dummy.CreateException();
            var exception3 = Dummy.CreateException();

            var failedChannelInformations = new List<FailedChannelInformation>();
            target.ChannelCreationFailedHandlers.Add(f =>
            {
                failedChannelInformations.Add(f);
                return TaskUtil.TrueCompletedTask;
            });

            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception1)
                .Throws(exception2)
                .Throws(exception3)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);

            // Assert
            actual.ShouldBe(responseCommand);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(4));
            _clientChannel.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
            failedChannelInformations.Count.ShouldBe(3);
            failedChannelInformations.Any(h => h.IsConnected).ShouldBeFalse();
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception1);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception2);
            failedChannelInformations.Select(e => e.Exception).ShouldContain(exception3);
        }

        [Test]
        [ExpectedException(typeof(ObjectDisposedException))]
        public async Task ProcessCommandAsync_ChannelDisposed_ThrowsObjectDisposed()
        {
            // Arrange            
            var requestCommand = Dummy.CreateCommand();
            var target = GetTarget();
            target.Dispose();

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);
        }

        [Test]
        [ExpectedException(typeof(TaskCanceledException))]
        public async Task ProcessCommandAsync_CanceledToken_ThrowsTaskCanceledException()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            responseCommand.Id = requestCommand.Id;
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            var target = GetTarget();
            var cts = new CancellationTokenSource();
            cts.Cancel();

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, cts.Token);
        }

        [Test]
        [ExpectedException(typeof(ApplicationException))]
        public async Task ProcessCommandAsync_ChannelCreationFailedHandlerReturnFalse_ThrowsException()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand(Dummy.CreatePlainDocument());
            var target = GetTarget();
            var exception = Dummy.CreateException<ApplicationException>();
            target.ChannelCreationFailedHandlers.Add(f => TaskUtil.FalseCompletedTask);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Throws(exception)
                .Returns(Task.FromResult(_clientChannel.Object));

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);
        }

        [Test]
        public async Task ProcessCommandAsync_ChannelOperationFailed_RecreateChannelAndProcesses()
        {
            // Arrange
            var requestCommand = Dummy.CreateCommand();
            var responseCommand = Dummy.CreateCommand(Dummy.CreatePing(), status: CommandStatus.Success);
            var sessionId = Guid.NewGuid();
            responseCommand.Id = requestCommand.Id;
            var target = GetTarget();
            var exception = Dummy.CreateException();
            var clientChannel2 = new Mock<IClientChannel>();

            FailedChannelInformation failedChannelInformation = null;
            target.ChannelOperationFailedHandlers.Add(f =>
            {
                failedChannelInformation = f;
                return TaskUtil.TrueCompletedTask;
            });
            _clientChannel
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
            clientChannel2
                .Setup(c => c.ProcessCommandAsync(It.IsAny<Command>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(responseCommand);
            _establishedClientChannelBuilder
                .SetupSequence(b => b.BuildAndEstablishAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.FromResult(_clientChannel.Object))
                .Returns(Task.FromResult(clientChannel2.Object));
            clientChannel2
                .SetupGet(c => c.SessionId)
                .Returns(sessionId);
            clientChannel2
                .SetupGet(c => c.Transport)
                .Returns(_transport.Object);
            clientChannel2
                .SetupGet(c => c.State)
                .Returns(SessionState.Established);

            // Act
            var actual = await target.ProcessCommandAsync(requestCommand, _cancellationToken);

            // Assert
            actual.ShouldBe(responseCommand);
            _establishedClientChannelBuilder.Verify(c => c.BuildAndEstablishAsync(It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            _clientChannel.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
            clientChannel2.Verify(c => c.ProcessCommandAsync(requestCommand, _cancellationToken), Times.Once());
            failedChannelInformation.Exception.ShouldBe(exception);          
        }

        [Test]
        public async Task FinishAsync_EstablishedChannel_SendFinishingAndAwaitsForFinishedSession()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            await target.SendMessageAsync(message);
            var session = Dummy.CreateSession(SessionState.Finished);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();
            _clientChannel
                .Setup(c => c.ReceiveFinishedSessionAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(session);                

            // Act
            await target.FinishAsync(_cancellationToken);

            // Assert
            _clientChannel.Verify(c => c.SendFinishingSessionAsync(), Times.Once);
            _clientChannel.Verify(c => c.ReceiveFinishedSessionAsync(_cancellationToken), Times.Once);
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
        }

        [Test]
        public async Task FinishAsync_NotEstablishedChannel_DoNotSendEnvelopes()
        {
            // Arrange
            var message = Dummy.CreateMessage(Dummy.CreatePlainDocument());
            var target = GetTarget();
            await target.SendMessageAsync(message);
            var session = Dummy.CreateSession(SessionState.Finished);
            _establishedClientChannelBuilder.ResetCalls();
            _clientChannel.ResetCalls();
            _clientChannel
                .SetupGet(c => c.State)
                .Returns(SessionState.Finished);

            // Act
            await target.FinishAsync(_cancellationToken);

            // Assert
            _clientChannel.Verify(c => c.SendFinishingSessionAsync(), Times.Never);
            _clientChannel.Verify(c => c.ReceiveFinishedSessionAsync(_cancellationToken), Times.Never);
            _disposableClientChannel.Verify(c => c.Dispose(), Times.Once);
        }
    }
}