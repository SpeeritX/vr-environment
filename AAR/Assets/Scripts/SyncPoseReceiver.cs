﻿using System;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

public class SyncPoseReceiver : SyncPose
{
#pragma warning disable CS0618 // Type or member is obsolete
    [Serializable]
    public class UnityEventPose : UnityEvent<Vector3, Quaternion> { }

    [SerializeField]
    protected bool updateTransform;

    [SerializeField]
    private Transform centerEyeCamera;

    private Vector3? positionShift = null;
    private Quaternion? rotationShift = null;

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
    }

    protected virtual void Update()
    {
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
                    break;
                case NetworkEventType.DisconnectEvent:
                    Debug.Log($"SyncPoseReceiver: Disconnected from the server");
                    connectionLost.Invoke();
                    break;
            }
        }
        catch (NullReferenceException) { } // This happens when nobody listens to the connection events
    }

    protected virtual Pose UpdateTransform()
    {
        Pose receivedPose = DeserializePose(messageBuffer, formatter);
        if (positionShift == null || rotationShift == null)
        {
            Debug.Log($"CenterEyeCamera position: {centerEyeCamera.position}");
            Debug.Log($"Camera position: {Camera.main.transform.position}");
            transform.position = centerEyeCamera.position;
            transform.rotation = centerEyeCamera.rotation;

            positionShift = receivedPose.position - transform.position;
            rotationShift = Quaternion.Inverse(transform.rotation) * receivedPose.rotation;
            Debug.Log($"Position shift: {positionShift}");
        }
        else if (positionShift != null && rotationShift != null)
        {
            transform.position = receivedPose.position - (Vector3)positionShift;
            transform.rotation = receivedPose.rotation * (Quaternion)rotationShift;
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
