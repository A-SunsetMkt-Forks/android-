﻿using Android.Bluetooth;
using Android.Bluetooth.LE;
using AndroidX.AppCompat.App;
using ShortDev.Microsoft.ConnectedDevices.Protocol;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.Authentication;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.DeviceInfo;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Control;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Discovery;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Encryption;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Platforms;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Session.AppControl;
using ShortDev.Networking;
using System.Diagnostics;
using System.Net.NetworkInformation;
using ConnectionRequest = ShortDev.Microsoft.ConnectedDevices.Protocol.Connection.ConnectionRequest;
using ManifestPermission = Android.Manifest.Permission;

namespace Nearby_Sharing_Windows
{
    [Activity(Label = "@string/app_name", Theme = "@style/AppTheme", ConfigurationChanges = Android.Content.PM.ConfigChanges.Orientation | Android.Content.PM.ConfigChanges.ScreenSize)]
    public class ReceiveActivity : AppCompatActivity, ICdpBluetoothHandler
    {
        BluetoothAdapter? _btAdapter;
        BluetoothAdvertisement? _bluetoothAdvertisement;
        protected override void OnCreate(Bundle savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            SetContentView(Resource.Layout.activity_mac_address);

            RequestPermissions(new[] {
                ManifestPermission.AccessFineLocation,
                ManifestPermission.AccessCoarseLocation,
                ManifestPermission.Bluetooth,
                ManifestPermission.BluetoothScan,
                ManifestPermission.BluetoothConnect,
                ManifestPermission.BluetoothAdvertise,
                ManifestPermission.AccessBackgroundLocation
            }, 0);

            //UdpAdvertisement advertisement = new();
            //advertisement.StartDiscovery();

            var service = (BluetoothManager)GetSystemService(BluetoothService)!;
            _btAdapter = service.Adapter!;

            string address = TryGetBtAddress(_btAdapter, out var exception) ?? "00:fa:21:3e:fb:19"; // "d4:38:9c:0b:ca:ae"; //

            _bluetoothAdvertisement = new(this);
            _bluetoothAdvertisement.OnDeviceConnected += BluetoothAdvertisement_OnDeviceConnected;
            _bluetoothAdvertisement.StartAdvertisement(new CdpDeviceAdvertiseOptions(
                DeviceType.Android,
                PhysicalAddress.Parse(address.Replace(":", "").ToUpper()),
                _btAdapter.Name!
            ));
        }

        public override void Finish()
        {
            _bluetoothAdvertisement?.StopAdvertisement();
            base.Finish();
        }

        [DebuggerHidden]
        public string? TryGetBtAddress(BluetoothAdapter adapter, out Exception? exception)
        {
            exception = null;

            try
            {
                var mServiceField = adapter.Class.GetDeclaredFields().FirstOrDefault((x) => x.Name.Contains("service", StringComparison.OrdinalIgnoreCase));
                if (mServiceField == null)
                    throw new MissingFieldException("No service field found!");

                mServiceField.Accessible = true;
                var serviceProxy = mServiceField.Get(adapter)!;
                var method = serviceProxy.Class.GetDeclaredMethod("getAddress");
                if (method == null)
                    throw new MissingMethodException("No method \"getAddress\"");

                method.Accessible = true;
                try
                {
                    return (string?)method.Invoke(serviceProxy);
                }
                catch (Java.Lang.Reflect.InvocationTargetException ex)
                {
                    if (ex.Cause == null)
                        throw;
                    throw ex.Cause;
                }
            }
            catch (System.Exception ex)
            {
                exception = ex;
            }
            return null;
        }

        long counter = 0;
        private void BluetoothAdvertisement_OnDeviceConnected(CdpRfcommSocket socket)
        {
            Task.Run(() =>
            {
                using (MemoryStream testStream = new())
                using (BigEndianBinaryWriter writer = new(socket.OutputStream!))
                using (BigEndianBinaryReader reader = new(socket.InputStream!))
                {
                    ulong localSessionId = 2;
                    CdpEncryptionInfo localEncryption = CdpEncryptionInfo.Create(CdpEncryptionParams.Default);

                    CdpCryptor? cryptor = null;
                    CdpEncryptionInfo? remoteEncryption = null;
                    while (true)
                    {
                        if (!CommonHeader.TryParse(reader, out var header, out _) || header == null)
                            return;

                        BinaryReader payloadReader = cryptor?.Read(reader, header) ?? reader;
                        {
                            header.CorrectClientSessionBit();

                            if (header.Type == MessageType.Connect)
                            {
                                header.SessionID |= localSessionId << 32;
                                ConnectionHeader connectionHeader = ConnectionHeader.Parse(payloadReader);
                                switch (connectionHeader.ConnectMessageType)
                                {
                                    case ConnectionType.ConnectRequest:
                                        {
                                            var connectionRequest = ConnectionRequest.Parse(payloadReader);
                                            remoteEncryption = CdpEncryptionInfo.FromRemote(connectionRequest.PublicKeyX, connectionRequest.PublicKeyY, connectionRequest.Nonce, CdpEncryptionParams.Default);

                                            var secret = localEncryption.GenerateSharedSecret(remoteEncryption);

                                            cryptor = new(secret);

                                            //header.AdditionalHeaders.Clear();

                                            header.Write(writer);

                                            new ConnectionHeader()
                                            {
                                                ConnectionMode = ConnectionMode.Proximal,
                                                ConnectMessageType = ConnectionType.ConnectResponse
                                            }.Write(writer);

                                            var publicKey = localEncryption.PublicKey;
                                            new ConnectionResponse()
                                            {
                                                Result = ConnectionResult.Pending,
                                                HMACSize = connectionRequest.HMACSize,
                                                MessageFragmentSize = connectionRequest.MessageFragmentSize,
                                                Nonce = localEncryption.Nonce,
                                                PublicKeyX = publicKey.X!,
                                                PublicKeyY = publicKey.Y!
                                            }.Write(writer);

                                            break;
                                        }
                                    case ConnectionType.DeviceAuthRequest:
                                    case ConnectionType.UserDeviceAuthRequest:
                                        {
                                            var authRequest = AuthenticationPayload.Parse(payloadReader);
                                            if (!authRequest.VerifyThumbprint(localEncryption.Nonce, remoteEncryption!.Nonce))
                                                throw new Exception("Invalid thumbprint");

                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, new ICdpWriteable[]
                                            {
                                                new ConnectionHeader()
                                                {
                                                    ConnectionMode = ConnectionMode.Proximal,
                                                    ConnectMessageType = connectionHeader.ConnectMessageType == ConnectionType.DeviceAuthRequest ?  ConnectionType.DeviceAuthResponse : ConnectionType.UserDeviceAuthResponse
                                                },
                                                AuthenticationPayload.Create(
                                                    localEncryption.DeviceCertificate!, // ToDo: User cert
                                                    localEncryption.Nonce, remoteEncryption!.Nonce
                                                )
                                            });

                                            break;
                                        }
                                    case ConnectionType.UpgradeRequest:
                                        {
                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, new ICdpWriteable[]
                                            {
                                                new ConnectionHeader()
                                                {
                                                    ConnectionMode = ConnectionMode.Proximal,
                                                    ConnectMessageType = ConnectionType.UpgradeFailure // We currently only support BT
                                                },
                                                new HResultPayload()
                                                {
                                                    HResult = 1 // Failure: Anything != 0
                                                }
                                            });
                                            break;
                                        }
                                    case ConnectionType.AuthDoneRequest:
                                        {
                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, new ICdpWriteable[]
                                            {
                                                new ConnectionHeader()
                                                {
                                                    ConnectionMode = ConnectionMode.Proximal,
                                                    ConnectMessageType = ConnectionType.AuthDoneRespone // Ack
                                                },
                                                new HResultPayload()
                                                {
                                                    HResult = 0 // No error
                                                }
                                            });
                                            break;
                                        }
                                    case ConnectionType.DeviceInfoMessage:
                                        {
                                            var msg = DeviceInfoMessage.Parse(payloadReader);

                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, new ICdpWriteable[]
                                            {
                                                new ConnectionHeader()
                                                {
                                                    ConnectionMode = ConnectionMode.Proximal,
                                                    ConnectMessageType = ConnectionType.DeviceInfoResponseMessage // Ack
                                                }
                                            });
                                            break;
                                        }
                                    default:
                                        {
                                            var type = connectionHeader.ConnectMessageType;
                                            break;
                                        }
                                }
                            }
                            else if (header.Type == MessageType.Control)
                            {
                                var controlHeader = ControlHeader.Parse(payloadReader);
                                switch (controlHeader.MessageType)
                                {
                                    case ControlMessageType.StartChannelRequest:
                                        {
                                            var msg = StartChannelRequest.Parse(payloadReader);

                                            header.SetReplyToId(header.RequestID);
                                            header.SequenceNumber += 1;

                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, (writer) =>
                                            {
                                                new ControlHeader()
                                                {
                                                    MessageType = ControlMessageType.StartChannelResponse
                                                }.Write(writer);
                                                writer.Write(BinaryConvert.ToBytes("0000000000000000000000000000"));
                                            });
                                            break;
                                        }
                                    default:
                                        break;
                                }
                            }
                            else if (header.Type == MessageType.Session)
                            {
                                var appControlHeader = AppControlHeader.Parse(payloadReader);
                                switch (appControlHeader.MessageType)
                                {
                                    case AppControlType.LaunchUri:
                                        {
                                            var abc = payloadReader.ReadPayload();
                                            header.SetReplyToId(header.RequestID);
                                            header.SequenceNumber += 1;

                                            header.Flags = 0;
                                            cryptor!.EncryptMessage(writer, header, (payloadWriter) =>
                                            {
                                                payloadWriter.Write(
                                                    BinaryConvert.ToBytes("0000000100000000000000012D120A0217530065006C006500630074006500640050006C006100740066006F0072006D00560065007200730069006F006E00100AC568010016560065007200730069006F006E00480061006E0064005300680061006B00650052006500730075006C007400100AC568010000")
                                                );
                                            });
                                            break;
                                        }
                                    default:
                                        break;
                                }
                            }
                            else
                            {

                            }
                        }

                        writer.Flush();
                    }
                }
            });
        }

        public Task ScanBLeAsync(CdpScanOptions<CdpBluetoothDevice> scanOptions, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<CdpRfcommSocket> ConnectRfcommAsync(CdpBluetoothDevice device, CdpRfcommOptions options, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public async Task AdvertiseBLeBeaconAsync(CdpAdvertiseOptions options, CancellationToken cancellationToken = default)
        {
            var settings = new AdvertiseSettings.Builder()
                .SetAdvertiseMode(AdvertiseMode.LowLatency)!
                .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
                .SetConnectable(false)!
                .Build();

            var data = new AdvertiseData.Builder()
                .AddManufacturerData(options.ManufacturerId, options.BeaconData!)!
                .Build();

            BLeAdvertiseCallback callback = new();
            _btAdapter!.BluetoothLeAdvertiser!.StartAdvertising(settings, data, callback);

            await AwaitCancellation(cancellationToken);

            _btAdapter.BluetoothLeAdvertiser.StopAdvertising(callback);
        }

        class BLeAdvertiseCallback : AdvertiseCallback { }

        public async Task ListenRfcommAsync(CdpRfcommOptions options, CancellationToken cancellationToken = default)
        {
            using (var listener = _btAdapter!.ListenUsingInsecureRfcommWithServiceRecord(
                options.ServiceName,
                Java.Util.UUID.FromString(options.ServiceId)
            )!)
            {
                await Task.Run(() =>
                {
                    while (true)
                    {
                        var socket = listener.Accept();
                        if (cancellationToken.IsCancellationRequested)
                            return;

                        if (socket != null)
                            options!.OnSocketConnected!(socket.ToCdp());
                    }
                }, cancellationToken);
                await AwaitCancellation(cancellationToken);
                listener.Close();
            }
        }

        Task AwaitCancellation(CancellationToken cancellationToken)
        {
            TaskCompletionSource<bool> promise = new();
            cancellationToken.Register(() => promise.SetResult(true));
            return promise.Task;
        }
    }

    static class Extensions
    {
        public static CdpBluetoothDevice ToCdp(this BluetoothDevice @this, byte[]? beaconData = null)
            => new()
            {
                Address = @this.Address,
                Alias = @this.Alias,
                Name = @this.Name,
                BeaconData = beaconData
            };

        public static CdpRfcommSocket ToCdp(this BluetoothSocket @this)
            => new()
            {
                InputStream = @this.InputStream,
                OutputStream = @this.OutputStream,
                RemoteDevice = @this.RemoteDevice!.ToCdp(),
                Close = () => @this.Close()
            };
    }
}