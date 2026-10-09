using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;
using static System.Buffers.Binary.BinaryPrimitives;

namespace SysBot.Base;

/// <summary>
/// Abstract class representing the communication over USB.
/// </summary>
public abstract class SwitchUSB : IConsoleConnection
{
    public string Name { get; }
    public string Label { get; set; }
    public bool Connected { get; protected set; }
    private int Port { get; }
    public UsbTransportGuard TransportGuard { get; } = new();

    protected SwitchUSB(int port)
    {
        Port = port;
        Name = Label = $"USB-{port}";
    }

    public void Log(string message) => LogInfo(message);
    public void LogInfo(string message) => LogUtil.LogInfo(message, Label);
    public void LogError(string message) => LogUtil.LogError(message, Label);

    private IUsbDevice? _device;
    private UsbEndpointReader? _reader;
    private UsbEndpointWriter? _writer;

    public int MaximumTransferSize { get; set; } = 0x1C0;
    public int BaseDelay { get; set; } = 1;
    public int DelayFactor { get; set; } = 1000;

    private readonly Lock _sync = new();
    private static readonly UsbContext UsbContext = new();

    public void Reset()
    {
        Disconnect();
        Connect();
    }

    public void Connect()
    {
        TransportGuard.Reset();
        _device = TryFind() ?? throw new Exception("USB device not found.");
        var usb = _device ?? throw new Exception("USB device not found.");
        lock (_sync)
        {
            if (usb.IsOpen)
                usb.Close();
            usb.Open();

            usb.SetConfiguration(1);
            bool resagain = usb.ClaimInterface(0);
            if (!resagain)
            {
                usb.ReleaseInterface(0);
                usb.ClaimInterface(0);
            }

            _reader = usb.OpenEndpointReader(ReadEndpointID.Ep01);
            _writer = usb.OpenEndpointWriter(WriteEndpointID.Ep01);
            Connected = true;
        }
    }

    private IUsbDevice? TryFind()
    {
        lock (UsbContext)
        {
            var finder = new UsbDeviceFinder
            {
                Vid = 0x057E,
                Pid = 0x3000,
            };

            using var devices = UsbContext.FindAll(finder);
            foreach (var device in devices)
            {
                // LibUsbDotNet 3.x no longer exposes the Windows registry information used by the old API.
                // LocationId.PortNumbers provides the USB topology instead; the final port number is the physical port number for the device.
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                    (device.LocationId.PortNumbers.Count == 0 ||
                     device.LocationId.PortNumbers[^1] != Port))
                {
                    continue;
                }

                // FindAll returns devices owned by the temporary collection.
                // Clone the selected device so it remains valid after the collection is disposed.
                return device.Clone();
            }
        }
        return null;
    }

    public void Disconnect()
    {
        lock (_sync)
        {
            if (_device is { IsOpen: true } openDevice)
            {
                openDevice.ReleaseInterface(0);
                openDevice.Close();
            }

            // LibUsbDotNet 3.x endpoint readers/writers do not expose Dispose().
            // Closing and disposing the device releases the underlying handle.
            _reader = null;
            _writer = null;
            _device?.Dispose();
            _device = null;
            Connected = false;
        }
    }

    public int Send(ReadOnlySpan<byte> buffer)
    {
        lock (_sync)
            return SendInternal(buffer);
    }

    public int Read(Span<byte> buffer)
    {
        lock (_sync)
            return ReadInternal(buffer);
    }

    protected byte[] Read(ICommandBuilder b, ulong offset, int length)
    {
        var cmd = b.Peek(offset, length, false);
        SendInternal(cmd);
        return ReadBulkUSB();
    }

    protected byte[] ReadMulti(ICommandBuilder b, IReadOnlyDictionary<ulong, int> offsetSizes)
    {
        var cmd = b.PeekMulti(offsetSizes, false);
        SendInternal(cmd);
        return ReadBulkUSB();
    }

    protected byte[] ReadBulkUSB()
    {
        TransportGuard.ThrowIfFaulted();
        // Give it time to push back.
        Thread.Sleep(1);

        lock (_sync)
        {
            TransportGuard.ThrowIfFaulted();
            try
            {
                if (_reader == null)
                    throw new Exception("USB device not found or not connected.");

                // Let usb-botbase tell us the response size.
                Span<byte> sizeOfReturn = stackalloc byte[4];
                var ec = _reader.Read(sizeOfReturn, 5000, out int ret);
                TransportGuard.ValidateTransfer(ec == Error.Success, ret, 4, true, "response header");
                if (ec != Error.Success && ret == 0)
                    throw new UsbException(ec);

                int size = ReadInt32LittleEndian(sizeOfReturn);
                TransportGuard.ValidateResponseSize(size);
                return ReadResult(size, _reader);
            }
            catch (Exception ex)
            {
                TransportGuard.RecordFailure(ex);
                // Win32Error is returned when the device aborts a transfer, which happens when, for example, readMem() is called with an invalid address.
                // As such, we ignore it to avoid log spam but still return a zero-buffer to avoid crashing the caller, and to maintain connection.
                var error = ex is UsbException usbEx ? usbEx.ErrorCode : Error.Other;
                if (error != Error.InvalidParam)
                    Log($"{nameof(ReadBulkUSB)} failed: {ex.Message}");
                return [0];
            }
        }
    }

    private byte[] ReadResult(int size, UsbEndpointReader reader)
    {
        var buffer = new byte[size];
        ReadResult(size, reader, buffer);
        return buffer;
    }

    private void ReadResult(int size, UsbEndpointReader reader, Span<byte> buffer)
    {
        // Loop until we have read everything.
        int transfSize = 0;
        while (transfSize < size)
        {
            Thread.Sleep(1);
            TransportGuard.ThrowIfFaulted();
            int requested = Math.Min(UsbEndpointReader.DefReadBufferSize, size - transfSize);
            var ec = reader.Read(buffer, transfSize, requested, 5000, out int lenVal);
            TransportGuard.ValidateTransfer(ec == Error.Success, lenVal, requested, false, "response payload");
            if (ec != Error.Success)
                throw new UsbException(ec);
            transfSize += lenVal;
        }
    }

    protected void Write(ICommandBuilder b, ReadOnlySpan<byte> data, ulong offset)
    {
        if (data.Length > MaximumTransferSize)
            WriteLarge(b, data, offset);
        else
            WriteSmall(b, data, offset);
    }

    public void WriteSmall(ICommandBuilder b, ReadOnlySpan<byte> data, ulong offset)
    {
        lock (_sync)
        {
            var cmd = b.Poke(offset, data, false);
            SendInternal(cmd);
            Thread.Sleep(1);
        }
    }

    private int ReadInternal(Span<byte> buffer)
    {
        TransportGuard.ThrowIfFaulted();
        try
        {
            if (_reader == null)
                throw new Exception("USB device not found or not connected.");

            Span<byte> sizeOfReturn = stackalloc byte[4];
            var ec = _reader.Read(sizeOfReturn, 5000, out int ret);
            TransportGuard.ValidateTransfer(ec == Error.Success, ret, 4, true, "response header");
            if (ec != Error.Success && ret == 0)
                throw new UsbException(ec);

            int size = ReadInt32LittleEndian(sizeOfReturn);
            TransportGuard.ValidateResponseSize(size, buffer.Length);
            ec = _reader.Read(buffer, 5000, out var lenVal);
            TransportGuard.ValidateTransfer(ec == Error.Success, lenVal, size, true, "response payload");
            if (ec != Error.Success)
                throw new UsbException(ec);

            return lenVal;
        }
        catch (Exception ex)
        {
            TransportGuard.RecordFailure(ex);
            // Win32Error is returned when the device aborts a transfer, which happens when, for example, readMem() is called with an invalid address.
            // As such, we ignore it to avoid log spam, log other exceptions, and return 0 to maintain connection.
            var error = ex is UsbException usbEx ? usbEx.ErrorCode : Error.Other;
            if (error != Error.InvalidParam)
                Log($"{nameof(ReadInternal)} failed: {ex.Message}");
            return 0;
        }
    }

    private int SendInternal(ReadOnlySpan<byte> buffer)
    {
        TransportGuard.ThrowIfFaulted();
        try
        {
            if (_writer == null)
                throw new Exception("USB device not found or not connected.");

            uint pack = (uint)buffer.Length + 2;
            Span<byte> tmp = stackalloc byte[4];
            WriteUInt32LittleEndian(tmp, pack);

            var ec = _writer.Write(tmp, 2000, out int ret);
            TransportGuard.ValidateTransfer(ec == Error.Success, ret, 4, true, "command header");
            if (ec != Error.Success && ret == 0)
                throw new UsbException(ec);

            ec = _writer.Write(buffer, 2000, out var l);
            TransportGuard.ValidateTransfer(ec == Error.Success, l, buffer.Length, true, "command payload");
            if (ec != Error.Success)
                throw new UsbException(ec);

            return l;
        }
        catch (Exception ex)
        {
            TransportGuard.RecordFailure(ex);
            // Win32Error is returned when the device aborts a transfer, which happens when, for example, readMem() is called with an invalid address.
            // As such, we ignore it to avoid log spam, log other exceptions, and return 0 to maintain connection.
            var error = ex is UsbException usbEx ? usbEx.ErrorCode : Error.Other;
            if (error != Error.InvalidParam)
                Log($"{nameof(SendInternal)} failed: {ex.Message}");
            return 0;
        }
    }

    private void WriteLarge(ICommandBuilder b, ReadOnlySpan<byte> data, ulong offset)
    {
        while (data.Length != 0)
        {
            var length = Math.Min(data.Length, MaximumTransferSize);
            var slice = data[..length];
            WriteSmall(b, slice, offset);

            data = data[length..];
            offset += (uint)length;
            Thread.Sleep((MaximumTransferSize / DelayFactor) + BaseDelay);
        }
    }
}
