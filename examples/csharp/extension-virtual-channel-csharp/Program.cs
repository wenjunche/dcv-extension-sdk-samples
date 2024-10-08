﻿// /*
//  * Copyright Amazon.com, Inc. or its affiliates. All Rights Reserved.
//  * SPDX-License-Identifier: MIT-0
//  *
//  * Permission is hereby granted, free of charge, to any person obtaining a copy of this
//  * software and associated documentation files (the "Software"), to deal in the Software
//  * without restriction, including without limitation the rights to use, copy, modify,
//  * merge, publish, distribute, sublicense, and/or sell copies of the Software, and to
//  * permit persons to whom the Software is furnished to do so.
//  *
//  * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
//  * INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
//  * PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
//  * HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
//  * OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
//  * SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//  */

using System;
using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DcvExtensionVirtualChannelsCS.DcvExtensions;
using Newtonsoft.Json;
using Openfin.Desktop;
using Openfin.Desktop.InteropAPI;
using Openfin.Desktop.Logging;
using Openfin.Desktop.Messaging;

namespace DcvExtensionVirtualChannelsCS
{
    /*
     * Std input is used to read messages from DCV
     * Std output is used to send messages to DCV
     */

    internal class Program
    {
        private const string VirtualChannelName = "echo";
        private const int DataChunk = 1024 * 100;
        private static Runtime _runtime;
        private static ChannelProvider _channelProvider;

        private static readonly string LogPath =
            $@"C:\Temp\DcvExtensionVirtualChannelsCS_{Process.GetCurrentProcess().Id}.log";
        private static readonly SimpleLogger logger = new SimpleLogger(LogPath);


        public static void Main()
        {
            //StartOpenFin(null);
            //Thread.Sleep(1000 * 5000);
            MainAsync().GetAwaiter().GetResult();
        }

        private static async Task MainAsync()
        {
            try
            {
                logger.Log("DCV Extension Virtual Channels C#");

                //System.Diagnostics.Debugger.Launch();

                // Processor packs/sends messages to DCV and receives/decodes messages from DCV 
                var processor = new Processor(Console.OpenStandardInput(), Console.OpenStandardOutput(), logger);

                // Get DCV info
                logger.Log("Requesting DCV info");
                var dcvInfo = await processor.GetDcvInfoAsync();
                logger.Log($"Connected to DCV {dcvInfo.DcvRole}");

                // Get path to the manifest
                logger.Log("Requesting manifest path");
                var manifestPath = await processor.GetManifestAsync();
                logger.Log($"Received manifest path: {manifestPath}");

                logger.Log("Requesting virtual channel");

                // Try to establish a virtual channel
                var svcResponse =
                    await processor.SetupVirtualChannelAsync(Process.GetCurrentProcess().Id, VirtualChannelName);

                logger.Log($"Relay to virtual channel is available, connecting named pipe {svcResponse.RelayPath}");

                // Connect to the named pipe of the virtual channel
                var virtualChannel = await VirtualChannel.ConnectAsync(svcResponse.RelayPath, logger);

                logger.Log("Named pipe connected, send auth token");

                // Send auth token to the named pipe of the virtual channel
                var authToken = svcResponse.VirtualChannelAuthToken.ToByteArray();
                await virtualChannel.WriteAsync(authToken, 0, authToken.Length);

                logger.Log("Sent auth token");

                await processor.WaitVirtualChannelReadyEventAsync();

                logger.Log("Named pipe ready, starting read and write tasks");

                // From now on we can send data to the virtual channel and receive from it

                // Read data from the named pipe and log it
                var readTask = Task.Run(async () =>
                {
                    var readBuffer = new byte[DataChunk];

                    for (var i = 0; i < 1000; ++i)
                    {
                        var numBytes = await virtualChannel.ReadAsync(readBuffer, 0, readBuffer.Length);
                        if (numBytes == 0)
                        {
                            logger.Log("zero read,  continue");
                            continue;
                        }

                        logger.Log("Received bytes: {0}",
                            BitConverter.ToString(readBuffer.AsSpan(0, numBytes).ToArray()));
                        var message = Encoding.UTF8.GetString(readBuffer);
                        if (_channelProvider != null)
                        {
                            logger.Log($"Broadcasting {message}");
                            _channelProvider.Broadcast("proxy-request", message);
                        }
                    }
                });

                // Write data on the named pipe
                var writeTask = Task.Run(async () =>
                {
                    // Virtual channels transfer binary data, in this example we transfer UTF8 strings converted to binary
                    var message = $"Extension message";
                    var bytes = Encoding.UTF8.GetBytes(message);
                    // Send data to the named pipe of the virtual channel
                    await virtualChannel.WriteAsync(bytes, 0, bytes.Length);
                    logger.Log("Sent bytes: {0}", BitConverter.ToString(bytes.AsSpan(0, bytes.Length).ToArray()));
                });

                StartOpenFin(virtualChannel);

                // Wait until one of the two tasks completes
                await Task.WhenAll(readTask, writeTask);

                // Close the virtual channel
                await processor.CloseVirtualChannelAsync(VirtualChannelName);

                // Close the named pipe
                virtualChannel.Close();

                // Close the processor
                processor.Close();
            }
            catch (Exception ex)
            {
                logger.Log($"Uncaught Exception: {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                logger.Log("Exiting");
            }
        }

        private static void Runtime_Connected()
        {
            logger.Log("Connected to Runtime");

        }

        private static void Runtime_Disconnected(object sender, EventArgs e)
        {
            logger.Log("Disconnected from Runtime");
        }

        private static void StartOpenFin(VirtualChannel virtualChannel)
        {
            logger.Log("Starting Runtime");
            var DotNetOptions = new RuntimeOptions()
            {
                UUID = "dcv_extenions_sdk_channel",
                Version = "stable"
            };

            _runtime = Runtime.GetRuntimeInstance(DotNetOptions);
            _runtime.Disconnected += Runtime_Disconnected;
            _runtime.Connect(async () =>
            {
                logger.Log("Connected to Runtime");
                _channelProvider = _runtime.InterApplicationBus.Channel.CreateProvider("DcvExtensionVirtualChannel");
                await _channelProvider.OpenAsync();
                logger.Log("Channel provider created");
                _channelProvider.RegisterTopic<Context>("proxy-request", (ctx) =>
                {
                    Task.Run(async () =>
                    {
                        var message = JsonConvert.SerializeObject(ctx);
                        logger.Log($"Proxy channel received: {message}");
                        var bytes = Encoding.UTF8.GetBytes(message);
                        if (virtualChannel != null)
                        {
                            await virtualChannel.WriteAsync(bytes, 0, bytes.Length);
                        } else
                        {
                            _channelProvider.Broadcast("proxy-request", ctx);
                        }
                    });
                });
            });
            logger.Log("After Runtime.connect");
        }

    }

}