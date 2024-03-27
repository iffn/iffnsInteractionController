﻿using BestHTTP.SecureProtocol.Org.BouncyCastle.Crypto;
using BestHTTP.SecureProtocol.Org.BouncyCastle.Utilities.IO.Pem;
using JetBrains.Annotations;
using System;
using UdonSharp;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.UI;
using VRC.SDKBase;
using VRC.SDKBase.Editor;
using VRC.Udon.Common;

namespace iffnsStuff.iffnsVRCStuff.InteractionController
{
    public enum InputStates
    {
        off,
        justOn,
        on,
        justOff
    }

    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [DefaultExecutionOrder(ExecutionOrder)]
    public class InteractionController : UdonSharpBehaviour
    {
        [PublicAPI] public const int ExecutionOrder = 0;

        /*
        Created with additional inputs from KitKat
        
        ToAdd:
        - Differentiate between mouse does or does not move when off screen
        - Recalibration key hint
        */

        //Assignments
        [SerializeField] FOVDetector linkedFOVDetector;
        [SerializeField] private RectTransform uiCursor;
        [SerializeField] private RectTransform uiCalibrator;
        [SerializeField] private float cursorSpeedUI;
        [SerializeField] Camera linkedGrabCamera;
        [SerializeField] Transform referenceTransform;
        [SerializeField] LayerMask interactionMask;
        [SerializeField] Transform leftPalmIndicator;
        [SerializeField] Transform rightPalmIndicator;
        [SerializeField] Transform leftIndexIndicator;
        [SerializeField] Transform rightIndexIndicator;

        //Fixed variables
        bool useIsGrab;
        bool inVR;
        VRCPlayerApi localPlayer;
        float vrInteractionDistance;
        bool useAndGrabAreTheSame;


        bool triggerHoldBehavior = true;
        bool grabHoldBehavior = true;


        //Runtime variables General
        int fixedUpdateCounter = 0;
        int fixedUpdateClearanceByLateUpdate;

        //Runtime variables VR
        InputStates leftTriggerState;
        InputStates rightTriggerState;
        InputStates leftGrabState;
        InputStates rightGrabState;

        Vector3 leftPalmInteractionPosition;
        Vector3 rightPalmInteractionPosition;
        Vector3 leftIndexInteractionPosition;
        Vector3 rightIndexInteractionPosition;

        InteractionElement previousLeftPalmObject;
        InteractionElement newLeftPalmObject;
        InteractionElement previousRightPalmObject;
        InteractionElement newRightPalmObject;
        InteractionElement previousLeftIndexObject;
        InteractionElement newLeftIndexObject;
        InteractionElement previousRightIndexObject;
        InteractionElement newRightIndexObject;

        Vector3 fingerInteractionOffset = 0.058f * Vector3.forward;
        Vector3 palmInteractionOffset = 0.01f * Vector3.down;

        //Runtime variables Desktop
        public InputStates desktopInputState;
        public InteractionElement previousDesktopElement;
        public InteractionElement newDesktopElement;
        Vector2 cursorPosition;
        const int canvasSizeY = 540;
        const float halfDeg2Rad = Mathf.Deg2Rad * 0.5f;
        bool calibrated = false;
        Vector3 localRayOrigin;
        Vector3 localRayDirection;

        public Vector3 WorldRayOrigin
        {
            get
            {
                return referenceTransform.InverseTransformPoint(localRayOrigin);
            }
        }

        public Vector3 WorldRayDirection
        {
            get
            {
                return referenceTransform.TransformDirection(localRayDirection);
            }
        }

        public Transform ReferenceTransform
        {
            set
            {
                referenceTransform = value;
            }
        }
        
        //Internal function
        static void UpdateRayInteraction(InteractionElement previousElement, InteractionElement newElement, InputStates inputState, Vector3 rayWorldOrigin, Vector3 rayWorldDirection)
        {
            switch (inputState)
            {
                case InputStates.off:
                    if (previousElement != newElement)
                    {
                        if (previousElement) previousElement.Highlight = false;
                        if (newElement) newElement.Highlight = true;
                    }
                    break;
                case InputStates.justOn:
                    if (previousElement) previousElement.InteractionStart(rayWorldOrigin, rayWorldDirection);
                    break;
                case InputStates.on:
                    if (previousElement) previousElement.UpdateElement(rayWorldOrigin, rayWorldDirection);
                    break;
                case InputStates.justOff:
                    if (previousElement != newElement)
                    {
                        if (previousElement) previousElement.Highlight = false;
                        if (newElement) newElement.Highlight = true;
                    }

                    if (previousElement) previousElement.InteractionStop();
                    break;
                default:
                    break;
            }
        }

        static void UpdateRayInteraction(InteractionElement previousElement, InteractionElement newElement, InputStates inputState, Vector3 worldPosition)
        {
            switch (inputState)
            {
                case InputStates.off:
                    if (previousElement != newElement)
                    {
                        if (previousElement) previousElement.Highlight = false;
                        if (newElement) newElement.Highlight = true;
                    }
                    break;
                case InputStates.justOn:
                    if (previousElement) previousElement.InteractionStart(worldPosition);
                    break;
                case InputStates.on:
                    if (previousElement) previousElement.UpdateElement(worldPosition);
                    break;
                case InputStates.justOff:
                    if (previousElement != newElement)
                    {
                        if (previousElement) previousElement.Highlight = false;
                        if (newElement) newElement.Highlight = true;
                    }

                    if (previousElement) previousElement.InteractionStop();
                    break;
                default:
                    break;
            }
        }

        InteractionElement GetInteractedObjectInVR(Vector3 worldPosition)
        {
            Collider[] colliders = Physics.OverlapSphere(worldPosition, vrInteractionDistance, interactionMask);

            foreach(Collider collider in colliders)
            {

                InteractionCollider potentialCollider = collider.transform.GetComponent<InteractionCollider>();

                if (potentialCollider)
                {
                    if (potentialCollider.WorldCollisionPointIsValid(worldPosition)) return potentialCollider.LinkedInteractionElement;
                    else return null;
                }

                /*
                //TryGetComponent not exposed in U# yet
                if (collider.transform.TryGetComponent(out InteractionCollider controller))
                    return controller.LinkedInteractionElement;
                */
            }

            return null;
        }

        InteractionElement GetInteractedObjectInDesktop(Vector3 origin, Vector3 direction)
        {
            if (Physics.Raycast(new Ray(origin, direction), out RaycastHit hit, Mathf.Infinity, interactionMask)) //ToDo: Limit interaction distance
            {
                if (hit.collider != null) //At least VRChat client sim canvas hit collider somehow null
                {
                    InteractionCollider potentialCollider = hit.transform.GetComponent<InteractionCollider>();

                    if (potentialCollider)
                    {
                        if (potentialCollider.WorldCollisionPointIsValid(hit.point)) return potentialCollider.LinkedInteractionElement;
                        else return null;
                    }
                }
            }

            return null;
        }

        void CalculateAvatarOffsets()
        {
            //Finger length, also used as reference for rest
            Vector3 fingerBase = localPlayer.GetBonePosition(HumanBodyBones.RightIndexProximal);
            Vector3 fingerMiddle = localPlayer.GetBonePosition(HumanBodyBones.RightIndexIntermediate);
            Vector3 fingerFront = localPlayer.GetBonePosition(HumanBodyBones.RightIndexDistal);

            float fingerLength = (fingerBase - fingerMiddle).magnitude + (fingerMiddle - fingerFront).magnitude;

            if (fingerLength == 0) fingerLength = localPlayer.GetAvatarEyeHeightAsMeters() * 0.058f;

            fingerInteractionOffset = fingerLength * Vector3.right;

            //Palm
            palmInteractionOffset = fingerLength * 0.3f * Vector3.down;

            //Indicator
            vrInteractionDistance = fingerLength * 0.2f;

            Vector3 indicatorScale = vrInteractionDistance * Vector3.one;

            leftIndexIndicator.localScale = indicatorScale;
            rightIndexIndicator.localScale = indicatorScale;
            leftPalmIndicator.localScale = indicatorScale;
            rightPalmIndicator.localScale = indicatorScale;
        }

        static InputStates GetNewInputState(InputStates existingState, bool newValue)
        {
            if (newValue)
            {
                if (existingState == InputStates.on || existingState == InputStates.justOn) return InputStates.on;
                else return InputStates.justOn;
            }
            else
            {
                if (existingState == InputStates.off || existingState == InputStates.justOff) return InputStates.off;
                else return InputStates.justOff;
            }
        }

        //Unity functions
        private void Start()
        {
            localPlayer = Networking.LocalPlayer;
            inVR = localPlayer.IsUserInVR();

            if (referenceTransform == null) referenceTransform = transform;

            useAndGrabAreTheSame = !inVR;

            string[] controllers = Input.GetJoystickNames();

            foreach (string controller in controllers)
            {
                if (!controller.ToLower().Contains("vive")) continue;

                useAndGrabAreTheSame = true;
                break;
            }

            if (!inVR)
            {
                leftIndexIndicator.gameObject.SetActive(false);
                rightIndexIndicator.gameObject.SetActive(false);
                leftPalmIndicator.gameObject.SetActive(false);
                rightPalmIndicator.gameObject.SetActive(false);
            }
        }

        private void Update()
        {
            if (inVR)
            {
                //ToDo: Replace with ref if available
                leftTriggerState = GetNewInputState(leftTriggerState, Input.GetAxis("Oculus_CrossPlatform_PrimaryIndexTrigger") > 0.9f);
                rightTriggerState = GetNewInputState(rightTriggerState, Input.GetAxis("Oculus_CrossPlatform_SecondaryIndexTrigger") > 0.9f);
                leftGrabState = GetNewInputState(leftGrabState, Input.GetAxis("Oculus_CrossPlatform_PrimaryHandTrigger") > 0.9f);
                rightGrabState = GetNewInputState(rightGrabState, Input.GetAxis("Oculus_CrossPlatform_SecondaryHandTrigger") > 0.9f);

                UpdateRayInteraction(previousLeftPalmObject, newLeftPalmObject, leftGrabState, leftPalmInteractionPosition);
                UpdateRayInteraction(previousRightPalmObject, newRightPalmObject, rightGrabState, rightPalmInteractionPosition);
                UpdateRayInteraction(previousLeftIndexObject, newLeftIndexObject, leftTriggerState, leftIndexInteractionPosition);
                UpdateRayInteraction(previousRightIndexObject, newRightIndexObject, rightTriggerState, rightPalmInteractionPosition);

                if (leftGrabState == InputStates.off || leftGrabState == InputStates.justOff) previousLeftPalmObject = newLeftPalmObject;
                if (leftTriggerState == InputStates.off || leftTriggerState == InputStates.justOff) previousLeftIndexObject = newLeftIndexObject;
                if (rightGrabState == InputStates.off || rightGrabState == InputStates.justOff) previousRightPalmObject = newRightPalmObject;
                if (rightTriggerState == InputStates.off || rightTriggerState == InputStates.justOff) previousRightIndexObject = newRightIndexObject;
            }
            else
            {
                desktopInputState = GetNewInputState(desktopInputState, Input.GetMouseButton(0));

                UpdateRayInteraction(previousDesktopElement, newDesktopElement, desktopInputState, WorldRayOrigin, WorldRayDirection);

                if (desktopInputState == InputStates.off || desktopInputState == InputStates.justOff) previousDesktopElement = newDesktopElement;
            }
        }

        //public override void PostLateUpdate()
        public override void PostLateUpdate()
        {
            fixedUpdateClearanceByLateUpdate = fixedUpdateCounter;

            if (inVR)
            {
                VRCPlayerApi.TrackingData leftHand = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.LeftHand);
                VRCPlayerApi.TrackingData rightHand = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.RightHand);
                Vector3 leftFingerPosition = localPlayer.GetBonePosition(HumanBodyBones.LeftIndexProximal);
                Quaternion leftFingerRotation = localPlayer.GetBoneRotation(HumanBodyBones.LeftIndexProximal);
                Vector3 rightFingerPosition = localPlayer.GetBonePosition(HumanBodyBones.RightIndexProximal);
                Quaternion rightFingerRotation = localPlayer.GetBoneRotation(HumanBodyBones.RightIndexProximal);

                leftPalmInteractionPosition = leftHand.position + leftHand.rotation * (-palmInteractionOffset);
                rightPalmInteractionPosition = rightHand.position + rightHand.rotation * palmInteractionOffset;
                leftIndexInteractionPosition = leftFingerPosition + leftHand.rotation * fingerInteractionOffset;
                rightIndexInteractionPosition = rightFingerPosition + rightHand.rotation * fingerInteractionOffset;

                leftIndexIndicator.position = leftIndexInteractionPosition;
                rightIndexIndicator.position = rightIndexInteractionPosition;
                leftPalmIndicator.position = leftPalmInteractionPosition;
                rightPalmIndicator.position = rightPalmInteractionPosition;

                newLeftIndexObject = GetInteractedObjectInVR(leftIndexInteractionPosition);
                newRightIndexObject = GetInteractedObjectInVR(rightIndexInteractionPosition);
                newLeftPalmObject = GetInteractedObjectInVR(leftPalmInteractionPosition);
                newRightPalmObject = GetInteractedObjectInVR(rightPalmInteractionPosition);
            }
            else
            {
                if (Input.GetKeyDown(KeyCode.Tab))
                {
                    cursorPosition = Vector2.zero;
                    uiCursor.gameObject.SetActive(calibrated);

                    uiCalibrator.gameObject.SetActive(!calibrated);
                }
                else if (Input.GetKeyUp(KeyCode.Tab))
                {
                    uiCursor.gameObject.SetActive(false);
                    uiCalibrator.gameObject.SetActive(false);
                }

                if (Input.GetKey(KeyCode.Tab))
                {
                    cursorPosition.x += Input.GetAxisRaw("Mouse X");
                    cursorPosition.y += Input.GetAxisRaw("Mouse Y");

                    Vector2 cursorPositionActual = cursorPosition * cursorSpeedUI;

                    if (!calibrated)
                    {
                        if (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Tilde))
                        {
                            float uiSensitivityX = uiCalibrator.localPosition.x / cursorPositionActual.x;
                            float uiSensitivityY = uiCalibrator.localPosition.y / cursorPositionActual.y;
                            cursorSpeedUI *= (uiSensitivityX + uiSensitivityY) / 2;
                            calibrated = true;
                            uiCalibrator.gameObject.SetActive(false);
                            uiCursor.gameObject.SetActive(true);
                        }
                    }
                    else
                    {
                        float aspectRatio = 1f * linkedGrabCamera.pixelWidth / linkedGrabCamera.pixelHeight;

                        cursorPositionActual.x = Mathf.Clamp(cursorPositionActual.x, -aspectRatio * canvasSizeY, aspectRatio * canvasSizeY);
                        cursorPositionActual.y = Mathf.Clamp(cursorPositionActual.y, -canvasSizeY, canvasSizeY);

                        uiCursor.anchoredPosition = cursorPositionActual;

                        VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);


                        Vector3 localHeading = new Vector3
                        (
                            cursorPositionActual.x,
                            cursorPositionActual.y,
                            540 / Mathf.Tan(linkedFOVDetector.DetectedFOV * halfDeg2Rad)
                        );

                        localRayOrigin = referenceTransform.InverseTransformPoint(head.position);
                        localRayDirection = referenceTransform.InverseTransformDirection(head.rotation * localHeading);

                        newDesktopElement = GetInteractedObjectInDesktop(head.position, head.rotation * localHeading);

                        if (Input.GetKeyDown(KeyCode.Q))
                        {
                            calibrated = false;
                            uiCursor.gameObject.SetActive(false);
                            uiCalibrator.gameObject.SetActive(true);
                        }
                    }
                }
                else
                {
                    VRCPlayerApi.TrackingData head = localPlayer.GetTrackingData(VRCPlayerApi.TrackingDataType.Head);

                    localRayOrigin = referenceTransform.InverseTransformPoint(head.position);
                    localRayDirection = referenceTransform.InverseTransformDirection(head.rotation * Vector3.forward);

                    newDesktopElement = GetInteractedObjectInDesktop(head.position, head.rotation * Vector3.forward);
                }
            }
        }

        public override void OnAvatarEyeHeightChanged(VRCPlayerApi player, float prevEyeHeightAsMeters)
        {
            if (!player.isLocal) return;

            CalculateAvatarOffsets();
        }

        public override void OnAvatarChanged(VRCPlayerApi player)
        {
            if (!player.isLocal) return;
            
            CalculateAvatarOffsets();
        }
    }
}