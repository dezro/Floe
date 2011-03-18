#include "Stdafx.h"
#include "AudioCaptureClient.h"

namespace Floe
{
	namespace Interop
	{
		using namespace System::Threading;
		const IID IID_IAudioCaptureClient = __uuidof(IAudioCaptureClient);

		AudioCaptureClient::AudioCaptureClient(AudioDevice^ device, int packetSize, int minBufferSize, ...array<WaveFormat^> ^conversions)
			: AudioClient(device)
		{
			IAudioCaptureClient *iacc;
			ThrowOnFailure(this->Client->GetService(IID_IAudioCaptureClient, (void**)&iacc));
			m_iacc = iacc;
			m_packetSize = packetSize;
			m_buffer = new BYTE[packetSize];
			m_converter = gcnew AudioConverter(System::Math::Max(this->BufferSizeInBytes, minBufferSize), this->Format, conversions);
			m_eventArgs = gcnew WritePacketEventArgs();
		}

		void AudioCaptureClient::OnCapture(int count, IntPtr buffer)
		{
			count *= this->FrameSize;
			int total = m_converter->Convert(buffer, count, buffer);
			BYTE *src = (BYTE*)(void*)buffer;
			while(total > 0)
			{
				int copied = System::Math::Min(m_packetSize - m_used, total);
				memcpy((void*)(m_buffer+m_used), src, copied);
				src += copied;
				m_used += copied;
				total -= copied;

				if(m_used == m_packetSize)
				{
					this->OnWritePacket((IntPtr)m_buffer);
					m_used = 0;
				}
			}
		}

		void AudioCaptureClient::OnWritePacket(IntPtr buffer)
		{
			m_eventArgs->Buffer = buffer;
			this->WritePacket(this, m_eventArgs);
		}

		void AudioCaptureClient::Loop()
		{
			DWORD taskIndex = 0;
			HANDLE taskHandle = AvSetMmThreadCharacteristics(TEXT("Audio"), &taskIndex);

			array<WaitHandle^>^ handles = { this->CancelHandle, this->BufferHandle };
			ThrowOnFailure(this->Client->Start());

			try
			{
				while(true)
				{
					switch(WaitHandle::WaitAny(handles))
					{
					case 0:
						return;
					case 1:
						this->CaptureBuffer();
						break;
					}
				}
			}
			finally
			{
				ThrowOnFailure(this->Client->Stop());
				if(taskHandle != 0)
				{
					AvRevertMmThreadCharacteristics(taskHandle);
				}
			}
		}

		void AudioCaptureClient::CaptureBuffer()
		{
			BYTE *buffer;
			int count, flags;
			ThrowOnFailure(m_iacc->GetBuffer(&buffer, (UINT32*)&count, (DWORD*)&flags, 0, 0));
			this->OnCapture(count, (IntPtr)buffer);
			ThrowOnFailure(m_iacc->ReleaseBuffer(count));
		}

		AudioCaptureClient::~AudioCaptureClient()
		{
			this->Stop();
			if(m_iacc != 0)
			{
				m_iacc->Release();
				m_iacc = 0;
			}
			if(m_buffer != 0)
			{
				delete m_buffer;
				m_buffer = 0;
			}
		}

		AudioCaptureClient::!AudioCaptureClient()
		{
			this->~AudioCaptureClient();
		}
	}
}
