﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Lime.Protocol.Network
{
    /// <summary>
    /// Utility extensions for the IChannel interface.
    /// </summary>
    public static class ChannelExtensions
    {
        /// <summary>
        /// Sends the envelope using the appropriate
        /// method for its type.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="channel"></param>
        /// <param name="envelope"></param>
        /// <returns></returns>
        public static async Task SendAsync<T>(this IChannel channel, T envelope) where T : Envelope
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));

            if (typeof(T) == typeof(Notification))
            {
                await channel.SendNotificationAsync(envelope as Notification).ConfigureAwait(false);
            }
            else if (typeof(T) == typeof(Message))
            {
                await channel.SendMessageAsync(envelope as Message).ConfigureAwait(false);
            }
            else if (typeof(T) == typeof(Command))
            {
                await channel.SendCommandAsync(envelope as Command).ConfigureAwait(false);
            }
            else if (typeof(T) == typeof(Session))
            {
                await channel.SendSessionAsync(envelope as Session).ConfigureAwait(false);
            }
            else
            {
                throw new ArgumentException("Invalid or unknown envelope type");
            }
        }

        /// <summary>
        /// Composes a command envelope with a get method for the specified resource.
        /// </summary>
        /// <param name="channel">The channel.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <param name="uri">todo: describe uri parameter on GetResourceAsync</param>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        /// <exception cref="LimeException">Returns an exception with the failure reason</exception>
        public static Task<TResource> GetResourceAsync<TResource>(this IChannel channel, LimeUri uri, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null) where TResource : Document, new()
        {
            return GetResourceAsync<TResource>(channel, uri, null, cancellationToken, unrelatedCommandHandler);
        }

        /// <summary>
        /// Composes a command envelope with a get method for the specified resource.
        /// </summary>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <param name="channel">The channel.</param>
        /// <param name="uri">The resource uri.</param>
        /// <param name="from">From.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        /// <exception cref="LimeException">Returns an exception with the failure reason</exception>
        public static async Task<TResource> GetResourceAsync<TResource>(this IChannel channel, LimeUri uri, Node from, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null) where TResource : Document
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            var requestCommand = new Command
            {
                From = from,
                Method = CommandMethod.Get,
                Uri = uri
            };

            var responseCommand = await ProcessCommandAsync(channel, requestCommand, cancellationToken, unrelatedCommandHandler).ConfigureAwait(false);
            if (responseCommand.Status == CommandStatus.Success)
            {
                return (TResource)responseCommand.Resource;
            }
            else if (responseCommand.Reason != null)
            {
                throw new LimeException(responseCommand.Reason.Code, responseCommand.Reason.Description);
            }
            else
            {
                throw new InvalidOperationException("An invalid command response was received");
            }
        }

        /// <summary>
        /// Sets the resource value asynchronous.
        /// </summary>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <param name="channel">The channel.</param>
        /// <param name="uri">The resource uri.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        public static Task SetResourceAsync<TResource>(this IChannel channel, LimeUri uri, TResource resource, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null) where TResource : Document
        {
            return SetResourceAsync(channel, uri, resource, null, cancellationToken, unrelatedCommandHandler);
        }

        /// <summary>
        /// Sets the resource value asynchronous.
        /// </summary>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <param name="channel">The channel.</param>
        /// <param name="uri">The resource uri.</param>
        /// <param name="resource">The resource.</param>
        /// <param name="from">From.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        /// <exception cref="LimeException"></exception>
        public static async Task SetResourceAsync<TResource>(this IChannel channel, LimeUri uri, TResource resource, Node from, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null) where TResource : Document
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (uri == null) throw new ArgumentNullException(nameof(uri));
            if (resource == null) throw new ArgumentNullException(nameof(resource));

            var requestCommand = new Command
            {
                From = from,
                Method = CommandMethod.Set,
                Uri = uri,
                Resource = resource
            };

            var responseCommand = await ProcessCommandAsync(channel, requestCommand, cancellationToken, unrelatedCommandHandler).ConfigureAwait(false);
            if (responseCommand.Status != CommandStatus.Success)
            {
                if (responseCommand.Reason != null)
                {
                    throw new LimeException(responseCommand.Reason.Code, responseCommand.Reason.Description);
                }
                else
                {
#if DEBUG
                    if (requestCommand == responseCommand)
                    {
                        throw new InvalidOperationException("The request and the response are the same instance");
                    }
#endif

                    throw new InvalidOperationException("An invalid command response was received");
                }
            }
        }

        /// <summary>
        /// Composes a command envelope with a
        /// delete method for the specified resource.
        /// </summary>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <param name="channel">The channel.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        /// <exception cref="LimeException">Returns an exception with the failure reason</exception>
        public static Task DeleteResourceAsync(this IChannel channel, LimeUri uri, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null)
        {
            return DeleteResourceAsync(channel, uri, null, cancellationToken, unrelatedCommandHandler);
        }

        /// <summary>
        /// Composes a command envelope with a
        /// delete method for the specified resource.
        /// </summary>
        /// <typeparam name="TResource">The type of the resource.</typeparam>
        /// <param name="channel">The channel.</param>
        /// <param name="resource">The resource.</param>
        /// <param name="from">From.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        /// <exception cref="System.ArgumentNullException">channel</exception>
        /// <exception cref="LimeException">Returns an exception with the failure reason</exception>
        public static async Task DeleteResourceAsync(this IChannel channel, LimeUri uri, Node from, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (uri == null) throw new ArgumentNullException(nameof(uri));

            var requestCommand = new Command
            {
                From = from,
                Method = CommandMethod.Delete,
                Uri = uri
            };

            var responseCommand = await ProcessCommandAsync(channel, requestCommand, cancellationToken, unrelatedCommandHandler).ConfigureAwait(false);
            if (responseCommand.Status != CommandStatus.Success)
            {
                if (responseCommand.Reason != null)
                {
                    throw new LimeException(responseCommand.Reason.Code, responseCommand.Reason.Description);
                }
                else
                {
                    throw new InvalidOperationException("An invalid command response was received");
                }
            }
        }

        /// <summary>
        /// Sends a command request through the 
        /// channel and awaits for the response.
        /// This method synchronizes the channel
        /// calls to avoid multiple command processing
        /// conflicts. 
        /// </summary>
        /// <param name="channel">The channel.</param>
        /// <param name="requestCommand">The command request.</param>
        /// <param name="cancellationToken"></param>
        /// <param name="unrelatedCommandHandler">Defines a handler for unexpected commands received while awaiting for the actual command response. If not provided, the method will throw an <exception cref="InvalidOperationException"> in these cases.</exception>.</param>
        /// <returns></returns>
        public static async Task<Command> ProcessCommandAsync(this IChannel channel, Command requestCommand, CancellationToken cancellationToken, Func<Command, Task> unrelatedCommandHandler = null)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            if (requestCommand == null) throw new ArgumentNullException(nameof(requestCommand));

            await channel.SendCommandAsync(requestCommand).ConfigureAwait(false);
            Command responseCommand = null;
            while (
                responseCommand == null || 
                !responseCommand.Id.Equals(requestCommand.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                responseCommand = await channel.ReceiveCommandAsync(cancellationToken).ConfigureAwait(false);

                if (responseCommand != null &&
                    responseCommand.Id != requestCommand.Id)
                {
                    if (unrelatedCommandHandler != null)
                    {
                        await unrelatedCommandHandler(responseCommand).ConfigureAwait(false);
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"A different command id response was received. Expected was '{requestCommand.Id}' but received was '{responseCommand.Id}'.");
                    }
                }
            }

            return responseCommand;


        }
    }
}