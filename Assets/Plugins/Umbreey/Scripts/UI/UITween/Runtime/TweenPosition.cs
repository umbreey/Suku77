using UnityEngine;

namespace Umbreey
{
	/// <summary>
	/// Tween the object's position.
	/// </summary>

	[AddComponentMenu("UGUI/Tween/Tween Position")]
	public class TweenPosition : UITweener
	{
		public Vector3 from;
		public Vector3 to;
		public bool _isJump;
		public float flyHeight;
		RectTransform mTrans;

		public RectTransform cachedTransform
		{
			get
			{
				if (mTrans == null) mTrans = transform as RectTransform;
				return mTrans;
			}
		}


		/// <summary>
		/// Tween's current value.
		/// </summary>
		public Vector3 value
		{
			get { return cachedTransform.anchoredPosition; }
			set { cachedTransform.anchoredPosition = value; }
		}

		void Awake()
		{
		}

		/// <summary>
		/// Tween the value.
		/// </summary>

		protected override void OnUpdate(float factor, bool isFinished)
		{
			value = from * (1f - factor) + to * factor;
			if (_isJump)
			{
				float parabola = 4 * factor * (1 - factor); // 范围 [0,1]
				value +=new Vector3(0,flyHeight * parabola,0);
			}
		}

		/// <summary>
		/// Start the tweening operation.
		/// </summary>

		static public TweenPosition Begin(GameObject go, float duration, Vector3 pos,bool isJump,float flyHeight)
		{
			TweenPosition comp = UITweener.Begin<TweenPosition>(go, duration);
			comp.from = comp.value;
			comp.to = pos;
			comp._isJump = isJump;
			comp.flyHeight = flyHeight;

			if (duration <= 0f)
			{
				comp.Sample(1f, true);
				comp.enabled = false;
			}

			return comp;
		}

		[ContextMenu("Set 'From' to current value")]
		public override void SetStartToCurrentValue()
		{
			from = value;
		}

		[ContextMenu("Set 'To' to current value")]
		public override void SetEndToCurrentValue()
		{
			to = value;
		}

		[ContextMenu("Assume value of 'From'")]
		void SetCurrentValueToStart()
		{
			value = from;
		}

		[ContextMenu("Assume value of 'To'")]
		void SetCurrentValueToEnd()
		{
			value = to;
		}
	}
}