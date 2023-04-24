using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using System.Buffers.Binary;
using System.Linq;


public class SyncPoseReceiver : SyncPose
{
#pragma warning disable CS0618 // Type or member is obsolete
    [Serializable]
    public class UnityEventPose : UnityEvent<Vector3, Quaternion> { }

    [SerializeField]
    protected bool updateTransform;

    [SerializeField]
    private Transform centerEyeCamera;
    [SerializeField]
    private Camera phoneCamera;

    private bool synchronized = false;
    private Vector3 initialPhonePosition;
    private Vector3 initialHeadsetPosition;
    private float initialPhoneRotation;
    private float initialHeadsetRotation;
    private float rotationShift = 0;

    [Header("Events")]
    public UnityEvent connectionEstablished;
    public UnityEvent connectionLost;
    public UnityEventPose newPoseReceived;

    private bool first = true;

    protected override void Start()
    {
        base.Start();
        Debug.Log("SyncPoseReceiver: Starting the receiver");
        // To be able to receive broadcast messages we have to specify broadcast credentials
        NetworkTransport.SetBroadcastCredentials(hostID, BROADCAST_CREDENTIALS_KEY, BROADCAST_CREDENTIALS_VERSION,
            BROADCAST_CREDENTIALS_SUBVERSION, out byte error);
        if ((NetworkError)error != NetworkError.Ok)
        {
            Debug.LogError($"SyncPoseReceiver: Couldn't set broadcast credentials because of {(NetworkError)error}. Disabling the script");
            enabled = false;
            return;
        }
    }

    protected virtual void Update()
    {
        if (first)
        {
            first = false;
            Debug.Log($"CenterEyeCamera position: {centerEyeCamera.position}");

            transform.position = centerEyeCamera.position;
            transform.rotation = centerEyeCamera.rotation;
        }
        try
        {
            var eventType = NetworkTransport.Receive(out int outHostID, out int outConnectionID, out int outChannelID,
                messageBuffer, messageBuffer.Length, out int actualMessageLength, out byte error);
            switch (eventType)
            {
                case NetworkEventType.Nothing:
                    // Nothing has happend. That's a good thing :-)
                    break;
                case NetworkEventType.BroadcastEvent:
                    Debug.Log($"SyncPoseReceiver: Received a broadcast message");
                    string address = GetIPAddress(outHostID);
                    if (address != null)
                        ConnectToTheServer(address);
                    break;
                case NetworkEventType.ConnectEvent:
                    Debug.Log($"SyncPoseReceiver: Connected to the server");
                    connectionEstablished.Invoke();
                    break;
                case NetworkEventType.DataEvent:
                    Debug.Log($"SyncPoseReceiver: Received a new pose");
                    Pose p = updateTransform ? UpdateTransform() : DeserializePose(messageBuffer, formatter);
                    newPoseReceived.Invoke(p.position, p.rotation);
                    SendImage();
                    break;
                case NetworkEventType.DisconnectEvent:
                    Debug.Log($"SyncPoseReceiver: Disconnected from the server");
                    connectionLost.Invoke();
                    break;
            }
        }
        catch (NullReferenceException) { } // This happens when nobody listens to the connection events
    }

    private void SendImage()
    {
        byte[] image = Capture();

        byte[] message1 = new byte[MESSAGE_LENGTH];
        byte[] imageLengthBytes = BitConverter.GetBytes(image.Length);
        Array.Copy(imageLengthBytes, 0, message1, 0, 4);
        int bytesToSend1 = Math.Min(image.Length, MESSAGE_LENGTH - 4);
        Array.Copy(image, 0, message1, 4, bytesToSend1);
        SendData(message1);

        int sentBytes = bytesToSend1;
        while (sentBytes < image.Length)
        {
            int bytesToSend = Math.Min(image.Length - sentBytes, MESSAGE_LENGTH);
            byte[] message = new byte[MESSAGE_LENGTH];
            Array.Copy(image, sentBytes, message, 0, bytesToSend);
            if (bytesToSend < MESSAGE_LENGTH)
            {
                message = message.Take(bytesToSend).ToArray();
            }
            SendData(message);
            sentBytes += bytesToSend;
        }
    }

    private bool SendData(byte[] data)
    {
        NetworkTransport.Send(hostID, сonnectionID, channelID, data, data.Length, out byte error);
        if ((NetworkError)error != NetworkError.Ok)
        {
            Debug.LogError($"SyncPoseReceiver: Couldn't send data over the network because of {(NetworkError)error}");
            return false;
        }
        return true;
    }

    public byte[] Capture()
    {
        RenderTexture activeRenderTexture = RenderTexture.active;
        RenderTexture.active = phoneCamera.targetTexture;

        phoneCamera.Render();
        Texture2D image = new Texture2D(phoneCamera.targetTexture.width, phoneCamera.targetTexture.height);
        image.ReadPixels(new Rect(0, 0, phoneCamera.targetTexture.width, phoneCamera.targetTexture.height), 0, 0);
        image.Apply();
        RenderTexture.active = activeRenderTexture;

        byte[] bytes = image.EncodeToJPG(30);
        Destroy(image);
        return bytes;

    }

    protected virtual Pose UpdateTransform()
    {
        Pose receivedPose = DeserializePose(messageBuffer, formatter);
        if (synchronized == false)
        {
            synchronized = true;
            Debug.Log($"Received pose position: {receivedPose.position}");
            Debug.Log($"CenterEyeCamera position: {centerEyeCamera.position}");

            transform.position = centerEyeCamera.position;
            transform.rotation = centerEyeCamera.rotation;

            initialHeadsetPosition = transform.position;
            initialPhonePosition = receivedPose.position;
            Quaternion headsetRotation = transform.rotation;
            initialPhoneRotation = receivedPose.rotation.eulerAngles.y;
            initialHeadsetRotation = headsetRotation.eulerAngles.y;
            rotationShift = receivedPose.rotation.eulerAngles.y - headsetRotation.eulerAngles.y;
            Debug.Log($"initialPhoneRotation: {initialPhoneRotation}");
            Debug.Log($"initialHeadsetRotation: {initialHeadsetRotation}");
            Debug.Log($"initialHeadsetPosition: {initialHeadsetPosition}");
            Debug.Log($"initialPhonePosition: {initialPhonePosition}");
        }
        else
        {
            Vector3 relativePosition = receivedPose.position - initialPhonePosition;
            Debug.Log($"Relative position: {relativePosition}");
            Vector3 rotatedRelativePosition = Quaternion.Euler(0, -rotationShift, 0) * relativePosition;
            Debug.Log($"Rotated relative position: {rotatedRelativePosition}");
            transform.position = initialHeadsetPosition + rotatedRelativePosition;

            Debug.Log($"Phone rotation before adjustment: {receivedPose.rotation.eulerAngles}");
            Quaternion relativeRotation = receivedPose.rotation * Quaternion.Euler(0, -initialPhoneRotation, 0); ;
            Debug.Log($"Relative phone rotation: {relativeRotation.eulerAngles}");
            transform.rotation = Quaternion.Euler(0, initialHeadsetRotation, 0) * relativeRotation;
            Debug.Log($"Phone rotation including headset starting position: {transform.rotation.eulerAngles}");
        }
        return receivedPose;
    }

    protected virtual int ConnectToTheServer(string address)
    {
        Debug.Log($"SyncPoseReceiver: Connecting to the server at {address}");
        сonnectionID = NetworkTransport.Connect(hostID, address, port, 0, out byte error);
        if ((NetworkError)error != NetworkError.Ok)
        {
            Debug.LogError($"SyncPoseReceiver: Couldn't connect to the server because of {(NetworkError)error}");
            return INVALID_CONNECTION;
        }

        return сonnectionID;
    }

    protected static string GetIPAddress(int hostID)
    {
        Debug.Log($"SyncPoseReceiver: Getting an IP address {hostID}");
        NetworkTransport.GetBroadcastConnectionInfo(hostID, out string address, out int port, out byte error);
        if ((NetworkError)error != NetworkError.Ok)
        {
            Debug.LogError($"SyncPoseReceiver: Couldn't get an IP address because of {(NetworkError)error}");
            return null;
        }

        return address;
    }

    protected static Pose DeserializePose(byte[] buffer, BinaryFormatter formatter)
    {
        using (var stream = new MemoryStream(buffer))
        {
            return (Pose)formatter.Deserialize(stream);
        }
    }
#pragma warning restore CS0618 // Type or member is obsolete
}
