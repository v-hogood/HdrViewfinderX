﻿using Android.Graphics;
using Android.OS;
using Android.Renderscripts;
using Android.Util;
using Android.Views;
using Java.Lang;

namespace HdrViewfinder
{
    //
    // Renderscript-based merger for an HDR viewfinder
    //
    public class ViewfinderProcessor
    {
        private Allocation mInputHdrAllocation;
        private Allocation mPrevAllocation;
        private Allocation mOutputAllocation;

        private Handler mProcessingHandler;
        private ScriptIntrinsicYuvToRGB mHdrYuvToRGBScript;
        private ScriptIntrinsicColorMatrix mHdrColorMatrixScript;
        private ScriptIntrinsicBlend mHdrBlendScript;

        public ProcessingTask mHdrTask;

        private int mMode;

        public const int ModeNormal = 0;
        public const int ModeHdr = 2;

        public ViewfinderProcessor(RenderScript rs, Size dimensions)
        {
            Type.Builder yuvTypeBuilder = new Type.Builder(rs, Element.YUV(rs));
            yuvTypeBuilder.SetX(dimensions.Width);
            yuvTypeBuilder.SetY(dimensions.Height);
            yuvTypeBuilder.SetYuvFormat((int) ImageFormatType.Yuv420888);
            mInputHdrAllocation = Allocation.CreateTyped(rs, yuvTypeBuilder.Create(),
                AllocationUsage.IoInput | AllocationUsage.Script);

            Type.Builder rgbTypeBuilder = new Type.Builder(rs, Element.RGBA_8888(rs));
            rgbTypeBuilder.SetX(dimensions.Width);
            rgbTypeBuilder.SetY(dimensions.Height);
            mPrevAllocation = Allocation.CreateTyped(rs, rgbTypeBuilder.Create(),
                AllocationUsage.Script);
            mOutputAllocation = Allocation.CreateTyped(rs, rgbTypeBuilder.Create(),
                AllocationUsage.IoOutput | AllocationUsage.Script);

            HandlerThread processingThread = new HandlerThread("ViewfinderProcessor");
            processingThread.Start();
            mProcessingHandler = new Handler(processingThread.Looper);

            mHdrYuvToRGBScript = ScriptIntrinsicYuvToRGB.Create(rs, Element.RGBA_8888(rs));
            mHdrColorMatrixScript = ScriptIntrinsicColorMatrix.Create(rs);
            mHdrColorMatrixScript.SetColorMatrix(new Matrix4f(new float[] { 0.5f, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0.5f }));
            mHdrBlendScript = ScriptIntrinsicBlend.Create(rs, Element.RGBA_8888(rs));

            mHdrTask = new ProcessingTask(this, mInputHdrAllocation, dimensions);

            SetRenderMode(ModeNormal);
        }

        public Surface GetInputHdrSurface()
        {
            return mInputHdrAllocation.Surface;
        }

        public void SetOutputSurface(Surface output)
        {
            mOutputAllocation.Surface = output;
        }

        public void SetRenderMode(int mode)
        {
            mMode = mode;
        }

        //
        // Simple class to keep track of incoming frame count,
        // and to process the newest one in the processing thread
        //
        public class ProcessingTask : Object, IRunnable, Allocation.IOnBufferAvailableListener
        {
            private int mPendingFrames = 0;
            private int mFrameCounter = 0;
            private Size mDimensions;

            private Allocation mInputAllocation;

            private ViewfinderProcessor mParent;

            public ProcessingTask(ViewfinderProcessor parent, Allocation input, Size dimensions)
            {
                mParent = parent;
                mInputAllocation = input;
                mInputAllocation.SetOnBufferAvailableListener(this);
                mDimensions = dimensions;
            }

            public void OnBufferAvailable(Allocation a)
            {
                lock(this)
                {
                    mPendingFrames++;
                    mParent.mProcessingHandler.Post(this);
                }
            }

            public void Run()
            {
                // Find out how many frames have arrived
                int pendingFrames;
                lock(this)
                {
                    pendingFrames = mPendingFrames;
                    mPendingFrames = 0;

                    // Discard extra messages in case processing is slower than frame rate
                    mParent.mProcessingHandler.RemoveCallbacks(this);
                }

                // Get to newest input
                for (int i = 0; i < pendingFrames; i++)
                {
                    mInputAllocation.IoReceive();
                }
                mFrameCounter += pendingFrames - 1;

                mParent.mHdrYuvToRGBScript.SetInput(mInputAllocation);

                // Run processing pass
                if (mParent.mMode != ModeNormal)
                {
                    if (mParent.mMode == ModeHdr)
                    {
                        mParent.mHdrColorMatrixScript.ForEach(mParent.mPrevAllocation, mParent.mOutputAllocation);
                    }
                    else
                    {
                        int cutPointX = (mFrameCounter & 1) == 0 ? 0 : mDimensions.Width / 2;
                        mParent.mOutputAllocation.Copy2DRangeFrom(cutPointX, 0, mDimensions.Width / 2, mDimensions.Height,
                            mParent.mPrevAllocation, cutPointX, 0);
                    }

                    mParent.mHdrYuvToRGBScript.ForEach(mParent.mPrevAllocation);

                    if (mParent.mMode == ModeHdr)
                    {
                        mParent.mHdrBlendScript.ForEachDstOver(mParent.mPrevAllocation, mParent.mOutputAllocation);
                    }
                    else
                    {
                        int cutPointX = (mFrameCounter & 1) == 1 ? 0 : mDimensions.Width / 2;
                        mParent.mOutputAllocation.Copy2DRangeFrom(cutPointX, 0, mDimensions.Width / 2, mDimensions.Height,
                            mParent.mPrevAllocation, cutPointX, 0);
                    }
                }
                else
                {
                    mParent.mHdrYuvToRGBScript.ForEach(mParent.mOutputAllocation);
                }
                mFrameCounter++;

                mParent.mOutputAllocation.IoSend();
            }
        }
    }
}
