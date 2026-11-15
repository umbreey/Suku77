using System;

namespace Umbreey
{
	static public class TimeHelper
	{
		readonly static private DateTime m_UtcTime;

		public static long ZoneOffset { get; set; }
		
		static TimeHelper()
		{
			m_UtcTime = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
		}

		static private bool m_Synced;
		static private DateTime m_UtcTimeAtSync;
		static private DateTime m_UtcRemoteTimeAtSync;

		static public DateTime Today
		{
			get
			{
				return Now.Date;
			}
		}
		
		static public DateTime UTC
		{
			get
			{
				DateTime now = DateTime.UtcNow;
				if (m_Synced) {
					var deltaTime = now - m_UtcTimeAtSync;
					now = m_UtcRemoteTimeAtSync + deltaTime;
				}
				return now;
			}
		}
		
		static public DateTime Now
		{
			get
			{
				return UTC.AddSeconds(-ZoneOffset);
			}
		}


		static public int Timestamp
		{
			get
			{
				var totalSeconds = (Now - m_UtcTime).TotalSeconds;
				return Convert.ToInt32(Math.Floor(totalSeconds));
			}
		}

		static public int UtcTimestamp
		{
			get
			{
				var totalSeconds=(UTC - m_UtcTime).TotalSeconds;
				return Convert.ToInt32(Math.Floor(totalSeconds));
			}
		}

		
		static public void Sync(double timestamp)
		{
			m_Synced = true;
			m_UtcTimeAtSync = DateTime.UtcNow;
			m_UtcRemoteTimeAtSync = m_UtcTime.AddSeconds(timestamp);
			
		}
		
		//将UTC时间戳转成本地时间戳
		// static public int UtcToLocalTimeStamp(int timestamp)
		// {
		// 	return Convert.ToInt32((m_UtcTime.AddSeconds(timestamp).ToLocalTime() - m_UtcTime).TotalSeconds);
		// }
		
		//将本地时间戳转成UTC时间戳
		// static public int LocalToUtcTimeStamp(int timestamp)
		// {
		// 	return Convert.ToInt32((m_UtcTime.ToLocalTime().AddSeconds(timestamp).ToUniversalTime() - m_UtcTime.ToLocalTime()).TotalSeconds);
		// }

		//获取时间戳对应的时间,若传入为utc时间戳则返回utc时间，若传入为本地时间戳则返回本地时间
		static public DateTime GetTimeData(long timestamp)
		{
			return m_UtcTime.AddSeconds(timestamp);
		}
		
		//传入utc时间戳,返回本地时区时间
		public static DateTime GetLocalTimeData(long timestamp)
		{
			return m_UtcTime.AddSeconds(timestamp - ZoneOffset);
		}

		//将UTC时间戳转成本地时间戳
		public static long UtcToLocalTimeStamp(long timestamp)
		{
			return timestamp - ZoneOffset;
		}

		//将本地时间戳转成UTC时间戳
		public static long LocalToUtcTimeStamp(long timestamp)
		{
			return timestamp + ZoneOffset;
		}

		public static bool IsNewDay(long lastTimeStamp)
		{
			var lastDay = GetTimeData(lastTimeStamp);
			return lastDay.Date < Today.Date;
		}
	}
}
