using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using System.Buffers.Binary;
using System.Linq;
using System.Collections;


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

    public static int synchronizationStage = 0;
    private float distanceMultiplier = 1;
    private Vector3 initialPhonePosition;
    private Vector3 initialHeadsetPosition;
    private float initialPhoneRotation;
    private float initialHeadsetRotation;
    private float rotationShift = 0;

    [Header("Events")]
    public UnityEvent connectionEstablished;
    public UnityEvent connectionLost;
    public UnityEventPose newPoseReceived;

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
        StartCoroutine(NetworkEventsListener());
    }

    IEnumerator NetworkEventsListener()
    {
        Debug.Log("SyncPoseServer: NetworkEventsListener started");
        int outHostID;
        int outConnectionID;
        int outChannelID;
        int actualMessageLength;
        byte error;
        var eventType = NetworkTransport.Receive(out outHostID, out outConnectionID, out outChannelID,
                messageBuffer, messageBuffer.Length, out actualMessageLength, out error);
        while (true)
        {
            switch (eventType)
            {
                case NetworkEventType.Nothing:
                    yield return new WaitForSeconds(0.01f);
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
                    Pose p = updateTransform ? UpdateTransform() : DeserializePose(messageBuffer, formatter);
                    newPoseReceived.Invoke(p.position, p.rotation);
                    SendImage();
                    break;
                case NetworkEventType.DisconnectEvent:
                    Debug.Log($"SyncPoseReceiver: Disconnected from the server");
                    connectionLost.Invoke();
                    synchronizationStage=0;
                    break;
            }

            eventType = NetworkTransport.Receive(out outHostID, out outConnectionID, out outChannelID,
            messageBuffer, messageBuffer.Length, out actualMessageLength, out error);
        }
    }

    private void SendImage()
    {
        byte[] image = Capture();

        byte[] message1 = new byte[MESSAGE_LENGTH];
        byte[] imageLengthBytes = BitConverter.GetBytes(image.Length);
        byte[] imageWidth = BitConverter.GetBytes(phoneCamera.targetTexture.width);
        byte[] imageHeight = BitConverter.GetBytes(phoneCamera.targetTexture.height);
        byte[] phoneDistance = BitConverter.GetBytes(CalculatePhoneDistance());
        Array.Copy(imageLengthBytes, 0, message1, 0, 4);
        Array.Copy(imageWidth, 0, message1, 4, 4);
        Array.Copy(imageHeight, 0, message1, 8, 4);
        Array.Copy(phoneDistance, 0, message1, 12, 4);
        int bytesToSend1 = Math.Min(image.Length, MESSAGE_LENGTH - 16);
        Array.Copy(image, 0, message1, 16, bytesToSend1);
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
        if (synchronizationStage == 0)
        {
            synchronizationStage = 1;
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
        else if (synchronizationStage == 1)
        {
            // Calculate distance of a phone from its initial position
            float phoneShiftLength = GetRelativePhoneShift(receivedPose).magnitude;
            Debug.Log($"Shift length: {phoneShiftLength}");
            // Calculate distance of a headset from its initial position
            Vector3 headsetShift = centerEyeCamera.position - initialHeadsetPosition;
            float headsetShiftLength = headsetShift.magnitude;
            Debug.Log($"Headset shift length: {headsetShiftLength}");
            // Calculate the distance multiplier
            if (phoneShiftLength != 0 && headsetShiftLength != 0)
            {
                distanceMultiplier = headsetShiftLength / phoneShiftLength;
            }

            Debug.Log($"Distance multiplier: {distanceMultiplier}");
            synchronizationStage = 2;
        }

        if (synchronizationStage == 2)
        {
            transform.position = initialHeadsetPosition + GetRelativePhoneShift(receivedPose);
            transform.rotation = Quaternion.Euler(0, initialHeadsetRotation, 0) * GetRelativePhoneRotation(receivedPose);
        }
        return receivedPose;
    }

    private Vector3 GetRelativePhoneShift(Pose receivedPose)
    {
        Vector3 relativeShift = (receivedPose.position - initialPhonePosition) * distanceMultiplier;
        Vector3 rotatedRelativeShift = Quaternion.Euler(0, -rotationShift, 0) * relativeShift;
        return rotatedRelativeShift;
    }

    private Quaternion GetRelativePhoneRotation(Pose receivedPose)
    {
        Quaternion relativeRotation = receivedPose.rotation * Quaternion.Euler(0, -initialPhoneRotation, 0); ;
        return relativeRotation;
    }

    private float CalculatePhoneDistance()
    {
        return (transform.position - centerEyeCamera.position).magnitude;
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
