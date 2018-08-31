using System;
using StandardAssets.Characters.CharacterInput;
using StandardAssets.Characters.Physics;
using UnityEngine;
using Util;

namespace StandardAssets.Characters.ThirdPerson
{
	[Serializable]
	public class RootMotionThirdPersonMotor : IThirdPersonMotor
	{
		/// <summary>
		/// Track distance above the ground at these frame intervals (to prevent checking every frame)
		/// </summary>
		private const int k_TrackGroundFrameIntervals = 5;
		
		//Serialized Fields
		[SerializeField]
		protected ThirdPersonRootMotionConfiguration configuration;

		[SerializeField]
		protected bool useRapidTurnForStrafeTransition = true;
		
		[SerializeField]
		protected InputResponse sprintInput;
		
		[SerializeField]
		protected CharacterRotator rotator;

		//Properties
		public float normalizedTurningSpeed { get; private set; }
		public float normalizedLateralSpeed { get; private set; }
		public float normalizedForwardSpeed { get; private set; }

		public float fallTime
		{
			get { return characterPhysics.fallTime; }
		}

		public float targetYRotation { get; private set; }
		
		public float cachedForwardVelocity { get; protected set; }
		/// <summary>
		/// Gets the character's current movement mode.
		/// </summary>
		/// <value>Either Action or Strafe.</value>
		public ThirdPersonMotorMovementMode movementMode { get; private set; }

		public Action jumpStarted { get; set; }
		public Action landed { get; set; }
		public Action<float> fallStarted { get; set; }
		public Action<float> rapidlyTurned { get; set; }

		//Protected fields

		/// <summary>
		/// The input implementation
		/// </summary>
		protected ICharacterInput characterInput;

		/// <summary>
		/// The physic implementation
		/// </summary>
		protected ICharacterPhysics characterPhysics;

		protected ThirdPersonAnimationController animationController;

		protected Animator animator;

		protected ThirdPersonGroundMovementState preTurnMovementState;
		protected ThirdPersonGroundMovementState movementState = ThirdPersonGroundMovementState.Walking;

		protected ThirdPersonAerialMovementState aerialState = ThirdPersonAerialMovementState.Grounded;

		protected SlidingAverage averageForwardVelocity;

		protected SlidingAverage actionAverageForwardInput, strafeAverageForwardInput, strafeAverageLateralInput;

		private float turnaroundMovementTime;
		private int postLandFramesToIgnore;
		private bool isTurningIntoStrafe,
					 jumpQueued;

		private Vector3 fallDirection;

		private Transform transform;
		private GameObject gameObject;
		private ThirdPersonBrain thirdPersonBrain;
		private SizedQueue<Vector2> previousInputs;
		
		/// <summary>
		/// Track height above the ground?
		/// </summary>
		private bool trackGroundHeight;

		public TurnaroundBehaviour currentTurnaroundBehaviour
		{
			get { return thirdPersonBrain.turnaround; }
		}

		public float normalizedVerticalSpeed
		{
			get { return characterPhysics.normalizedVerticalSpeed; }
		}

		public ThirdPersonRootMotionConfiguration thirdPersonConfiguration
		{
			get { return configuration; }
		}
		
		public bool sprint { get; private set; }

		public ThirdPersonGroundMovementState currentGroundMovementState
		{
			get { return movementState; }
		}

		public ThirdPersonAerialMovementState currentAerialMovementState
		{
			get { return aerialState; }
		}

		public void OnJumpAnimationComplete()
		{
			var baseCharacterPhysics = characterPhysics as BaseCharacterPhysics;
			if (baseCharacterPhysics == null)
			{
				return;
			}

			var distance = baseCharacterPhysics.GetPredictedFallDistance();
			if (distance <= configuration.maxFallDistanceToLand)
			{
				OnLanding();
			}
		}

		private bool IsGrounded
		{
			get { return aerialState == ThirdPersonAerialMovementState.Grounded; }
		}

		//Unity Messages
		public void OnAnimatorMove()
		{
			if (movementState == ThirdPersonGroundMovementState.TurningAround)
			{
				characterPhysics.Move(thirdPersonBrain.turnaround.GetMovement(), Time.deltaTime);
				return;
			}

			if (animationController.isRootMotionState)
			{
				Vector3 groundMovementVector = animator.deltaPosition * configuration.scaleRootMovement;
				groundMovementVector.y = 0;
				characterPhysics.Move(groundMovementVector, Time.deltaTime);
				
				//Update the average movement speed
				if (postLandFramesToIgnore <= 0)
				{
					float movementVelocity = groundMovementVector.
											 GetMagnitudeOnAxis(transform.forward)/Time.deltaTime;
					if (movementVelocity > 0)
					{
						averageForwardVelocity.Add(movementVelocity, HandleNegative.Absolute);
					}
				}
				else
				{
					postLandFramesToIgnore--;
				}
			}
			else //airborne
			{
				if (normalizedVerticalSpeed <= 0 || aerialState != ThirdPersonAerialMovementState.Grounded)
				{
					CalculateFallForwardSpeed();
				}

				var movementDirection = movementMode == ThirdPersonMotorMovementMode.Action ? transform.forward :
					CalculateLocalInputDirection() ;
				fallDirection = Vector3.Lerp(fallDirection, movementDirection, configuration.fallDirectionChange);
				characterPhysics.Move(cachedForwardVelocity * Time.deltaTime * fallDirection * 
									  configuration.scaledGroundVelocity, Time.deltaTime);
			}
		}

		private void CalculateFallForwardSpeed()
		{
			float maxFallForward = configuration.fallingForwardSpeed;
			float target = maxFallForward * Mathf.Clamp01(characterInput.moveInput.magnitude);
			float time = cachedForwardVelocity > target
				? configuration.fallSpeedDeceleration
				: configuration.fallSpeedAcceleration;
			cachedForwardVelocity = Mathf.Lerp(cachedForwardVelocity, target, time);
			normalizedForwardSpeed = cachedForwardVelocity / maxFallForward;
		}

		public void Init(ThirdPersonBrain brain)
		{
			gameObject = brain.gameObject;
			transform = brain.transform;
			thirdPersonBrain = brain;
			characterInput = brain.inputForCharacter;
			characterPhysics = brain.physicsForCharacter;
			animator = gameObject.GetComponent<Animator>();
			animationController = brain.animationControl;
			averageForwardVelocity = new SlidingAverage(configuration.jumpGroundVelocityWindowSize);
			actionAverageForwardInput = new SlidingAverage(configuration.forwardInputWindowSize);
			strafeAverageForwardInput = new SlidingAverage(configuration.strafeInputWindowSize);
			strafeAverageLateralInput = new SlidingAverage(configuration.strafeInputWindowSize);
			previousInputs = new SizedQueue<Vector2>(configuration.bufferSizeInput);
			movementMode = ThirdPersonMotorMovementMode.Action;

			if (sprintInput != null)
			{
				sprintInput.Init();
			}

			OnStrafeEnded();
		}

		/// <summary>
		/// Subscribe
		/// </summary>
		public void Subscribe()
		{
			//Physics subscriptions
			characterPhysics.landed += OnLanding;
			characterPhysics.startedFalling += OnStartedFalling;

			//Input subscriptions
			characterInput.jumpPressed += OnJumpPressed;
			
			if (thirdPersonBrain.thirdPersonCameraAnimationManager != null)
			{
				thirdPersonBrain.thirdPersonCameraAnimationManager.forwardLockedModeStarted += OnStrafeStarted;
				thirdPersonBrain.thirdPersonCameraAnimationManager.forwardUnlockedModeStarted += OnStrafeEnded;
			}
			
			if (sprintInput != null)
			{
				sprintInput.started += OnSprintStarted;
				sprintInput.ended += OnSprintEnded;
			}

			//Turnaround subscription for runtime support
			foreach (TurnaroundBehaviour turnaroundBehaviour in thirdPersonBrain.turnaroundOptions)
			{
				turnaroundBehaviour.turnaroundComplete += TurnaroundComplete;
			}
		}

		private void OnSprintStarted()
		{
			sprint = !sprint;
		}
		
		private void OnSprintEnded()
		{
			sprint = false;
		}

		/// <summary>
		/// Unsubscribe
		/// </summary>
		public void Unsubscribe()
		{
			//Physics subscriptions
			if (characterPhysics != null)
			{
				characterPhysics.landed -= OnLanding;
				characterPhysics.startedFalling -= OnStartedFalling;
			}

			//Input subscriptions
			if (characterInput != null)
			{
				characterInput.jumpPressed -= OnJumpPressed;
			}

			if (thirdPersonBrain.thirdPersonCameraAnimationManager != null)
			{
				thirdPersonBrain.thirdPersonCameraAnimationManager.forwardLockedModeStarted -= OnStrafeStarted;
				thirdPersonBrain.thirdPersonCameraAnimationManager.forwardUnlockedModeStarted -= OnStrafeEnded;
			}
			
			if (sprintInput != null)
			{
				sprintInput.started -= OnSprintStarted;
				sprintInput.ended -= OnSprintEnded;
			}

			//Turnaround un-subscription for runtime support
			foreach (TurnaroundBehaviour turnaroundBehaviour in thirdPersonBrain.turnaroundOptions)
			{
				turnaroundBehaviour.turnaroundComplete -= TurnaroundComplete;
			}
		}

		public void Update()
		{
			if (configuration.autoToggleSprintOnNoInput && sprint && !characterInput.hasMovementInput)
			{
				sprint = false;
			}
			
			HandleMovement();
			previousInputs.Add(characterInput.moveInput);
			if (jumpQueued)
			{
				jumpQueued = TryJump();
			}

			if (trackGroundHeight)
			{
				UpdateTrackGroundHeight();
			}
		}

		/// <summary>
		/// Track height above ground when the physics character is in the air, but the animation has not yet changed to the fall animation.
		/// </summary>
		private void UpdateTrackGroundHeight()
		{
			if (aerialState == ThirdPersonAerialMovementState.Grounded && 
				!characterPhysics.isGrounded)
			{
				if (Time.frameCount % k_TrackGroundFrameIntervals == 0)
				{
					var baseCharacterPhysics = characterPhysics as BaseCharacterPhysics;
					if (baseCharacterPhysics != null)
					{
						float distance = baseCharacterPhysics.GetPredictedFallDistance();
						if (distance > configuration.maxFallDistanceToLand)
						{
							OnStartedFalling(distance);
						}
					}
					else
					{
						trackGroundHeight = false;
					}
				}
			}
			else
			{
				trackGroundHeight = false;
			}
		}

		//Protected Methods
		/// <summary>
		/// Handles player landing
		/// </summary>
		protected virtual void OnLanding()
		{
			aerialState = ThirdPersonAerialMovementState.Grounded;

			if (!characterInput.hasMovementInput)
			{
				averageForwardVelocity.Clear();
			}

			if (landed != null)
			{
				landed();
			}
		}

		/// <summary>
		/// Handles player falling
		/// </summary>
		/// <param name="predictedFallDistance"></param>
		protected virtual void OnStartedFalling(float predictedFallDistance)
		{
			// check if far enough from ground to enter fall state
			if (predictedFallDistance < configuration.maxFallDistanceToLand)
			{
				trackGroundHeight = true;
				return;
			}
			trackGroundHeight = false;
			
			if (aerialState == ThirdPersonAerialMovementState.Grounded)
			{
				cachedForwardVelocity = averageForwardVelocity.average;
			}
			
			aerialState = ThirdPersonAerialMovementState.Falling;
			
			if (fallStarted != null)
			{
				fallStarted(predictedFallDistance);
			}
		}

		/// <summary>
		/// Subscribes to the Jump action on input
		/// </summary>
		protected virtual void OnJumpPressed()
		{
			jumpQueued = true;
		}

		/// <summary>
		/// Method called by strafe input started
		/// </summary>
		protected virtual void OnStrafeStarted()
		{
			if (movementMode == ThirdPersonMotorMovementMode.Strafe)
			{
				return;
			}
			
			movementMode = ThirdPersonMotorMovementMode.Strafe;
			isTurningIntoStrafe = true;
		}

		/// <summary>
		/// Method called by strafe input ended
		/// </summary>
		protected virtual void OnStrafeEnded()
		{
			movementMode = ThirdPersonMotorMovementMode.Action;
		}

		/// <summary>
		/// Called by update to handle movement
		/// </summary>
		protected virtual void HandleMovement()
		{
			if (movementState == ThirdPersonGroundMovementState.TurningAround)
			{
				CalculateForwardMovement();
				return;
			}

			switch (movementMode)
			{
				case ThirdPersonMotorMovementMode.Action:
					ActionMovement();
					break;
				case ThirdPersonMotorMovementMode.Strafe:
					StrafeMovement();
					break;
			}
		}

		protected virtual void ActionMovement()
		{
			SetLookDirection();
			CalculateForwardMovement();
		}

		protected virtual void StrafeMovement()
		{
			if (!isTurningIntoStrafe)
			{
				SetStrafeLookDirection();
			}
			else
			{
				SetStartStrafeLookDirection();
			}

			CalculateStrafeMovement();
		}

		protected virtual void SetStrafeLookDirection()
		{
			Quaternion targetRotation = CalculateTargetRotation(0, 1);

			targetYRotation = targetRotation.eulerAngles.y;

			Quaternion newRotation =
				Quaternion.RotateTowards(transform.rotation, targetRotation,
										 configuration.turningYSpeed * Time.deltaTime);

			SetTurningSpeed(transform.rotation, newRotation);

			transform.rotation = newRotation;
		}

		protected virtual void SetLookDirection()
		{
			if (!characterInput.hasMovementInput)
			{
				normalizedTurningSpeed = 0;
				targetYRotation = transform.eulerAngles.y;
				return;
			}

			Quaternion targetRotation = CalculateTargetRotation();
			targetYRotation = targetRotation.eulerAngles.y;

			if (IsGrounded && CheckForAndHandleRapidTurn(targetRotation))
			{
				return;
			}

			float turnSpeed = IsGrounded
				? configuration.turningYSpeed
				: configuration.jumpTurningYSpeed;

			rotator.Tick(targetYRotation);
			Quaternion newRotation = rotator.GetNewRotation(transform, targetRotation, turnSpeed);

			SetTurningSpeed(transform.rotation, newRotation);

			transform.rotation = newRotation;
		}

		protected virtual void SetStartStrafeLookDirection()
		{
			SetStrafeLookDirection();
		}

		protected virtual void CalculateForwardMovement()
		{
			if (movementState == ThirdPersonGroundMovementState.TurningAround && turnaroundMovementTime < configuration.ignoreInputTimeRapidTurn)
			{
				turnaroundMovementTime += Time.deltaTime;
				return; 
			}
			
			normalizedLateralSpeed = 0;

			var inputVector = characterInput.moveInput;
			if (inputVector.magnitude > 1)
			{
				inputVector.Normalize();
			}
			actionAverageForwardInput.Add(inputVector.magnitude + (sprint && characterInput.hasMovementInput
											  ? configuration.sprintNormalizedForwardSpeedIncrease : 0));
			
			normalizedForwardSpeed = actionAverageForwardInput.average;

			// evaluate if current forward speed should be recorded for jump speed
			if (!IsGrounded || !animationController.isRootMotionState || 
				movementState == ThirdPersonGroundMovementState.TurningAround)
			{
				return;
			}
		}

		protected virtual void CalculateStrafeMovement()
		{
			strafeAverageForwardInput.Add(characterInput.moveInput.y);
			float averageForwardInput = strafeAverageForwardInput.average;
			strafeAverageLateralInput.Add(characterInput.moveInput.x);
			float averageLateralInput = strafeAverageLateralInput.average;
			
			normalizedForwardSpeed =
				Mathf.Clamp((Mathf.Approximately(averageForwardInput, 0f) ? 0f : averageForwardInput),
							-configuration.normalizedBackwardStrafeSpeed, configuration.normalizedForwardStrafeSpeed);
			normalizedLateralSpeed = Mathf.Approximately(averageLateralInput, 0f)
				? 0f : averageLateralInput * configuration.normalizedLateralStrafeSpeed;
		}

		protected virtual Quaternion CalculateTargetRotation()
		{
			return CalculateTargetRotation(characterInput.moveInput.x, characterInput.moveInput.y);
		}

		protected virtual Vector3 CalculateLocalInputDirection()
		{
			var localMovementDirection = new Vector3(characterInput.moveInput.x, 0f, characterInput.moveInput.y);
			return Quaternion.AngleAxis(thirdPersonBrain.bearingOfCharacter.cameraMain.eulerAngles.y, Vector3.up) * 
			       localMovementDirection.normalized;
		}

		protected virtual Quaternion CalculateTargetRotation(float x, float y)
		{
			Vector3 flatForward = thirdPersonBrain.bearingOfCharacter.CalculateCharacterBearing();
			Vector3 localMovementDirection = new Vector3(x, 0f, y);
			
			Quaternion cameraToInputOffset = Quaternion.FromToRotation(Vector3.forward, localMovementDirection);
			cameraToInputOffset.eulerAngles = new Vector3(0f, cameraToInputOffset.eulerAngles.y, 0f);

			return Quaternion.LookRotation(cameraToInputOffset * flatForward);
		}

		protected virtual void SetTurningSpeed(Quaternion currentRotation, Quaternion newRotation)
		{
			float currentY = currentRotation.eulerAngles.y;
			float newY = newRotation.eulerAngles.y;
			float difference = (MathUtilities.Wrap180(newY) - MathUtilities.Wrap180(currentY)) / Time.deltaTime;

			normalizedTurningSpeed = Mathf.Lerp(normalizedTurningSpeed,
												Mathf.Clamp(
													difference / configuration.turningYSpeed *
													configuration.turningSpeedScaleVisual, -1, 1),
												Time.deltaTime * configuration.turningLerpFactor);
		}

		protected virtual void TurnaroundComplete()
		{
			movementState = preTurnMovementState;
		}

		protected virtual bool CheckForAndHandleRapidTurn(Quaternion target)
		{
			if (thirdPersonBrain.turnaround == null)
			{
				return false;
			}

			
			float angle;

			if (ShouldTurnAround(out angle, target))
			{
				turnaroundMovementTime = 0f;
				cachedForwardVelocity = averageForwardVelocity.average;
				preTurnMovementState = movementState;
				movementState = ThirdPersonGroundMovementState.TurningAround;
				thirdPersonBrain.turnaround.TurnAround(angle);
				return true;
			}

			return false;
		}

		protected virtual bool ShouldTurnAround(out float angle, Quaternion target)
		{
			if (Mathf.Approximately(normalizedForwardSpeed, 0))
			{
				previousInputs.Clear();
				float currentY = transform.eulerAngles.y;
				float newY = target.eulerAngles.y;
				angle = MathUtilities.Wrap180(newY - currentY);
				return Mathf.Abs(angle) > configuration.stationaryAngleRapidTurn;
			}

			foreach (Vector2 previousInputsValue in previousInputs.values)
			{
				angle = MathUtilities.Wrap180(Vector2Utilities.Angle(previousInputsValue, characterInput.moveInput));
				float deltaMagnitude = Mathf.Abs(previousInputsValue.magnitude - characterInput.moveInput.magnitude);
				if (Mathf.Abs(angle) > configuration.inputAngleRapidTurn && deltaMagnitude < 0.25f)
				{
					previousInputs.Clear();
					return true;
				}
			}
			angle = 0;
			return false;
		}
		
		/// <summary>
		/// Attempts a jump
		/// </summary>
		/// <returns>True if a jump should be re-attempted</returns>
		private bool TryJump()
		{
			if (movementState == ThirdPersonGroundMovementState.TurningAround || 
				animationController.state == AnimationState.Landing)
			{
				return true;
			}
			if (!IsGrounded || characterPhysics.startedSlide || !animationController.isRootMotionState)
			{
				return false;
			}
			
			aerialState = ThirdPersonAerialMovementState.Jumping;
			
			if (Mathf.Abs(normalizedLateralSpeed) <= normalizedForwardSpeed && normalizedForwardSpeed >=0)
			{
				if (characterInput.moveInput.magnitude > configuration.standingJumpMinInputThreshold && 
					animator.deltaPosition.GetMagnitudeOnAxis(transform.forward) <= 
					configuration.standingJumpMaxMovementThreshold * Time.deltaTime)
				{
					cachedForwardVelocity = configuration.standingJumpSpeed;
					normalizedForwardSpeed = 1;
					animationController.UpdateForwardSpeed(normalizedForwardSpeed, 1);
				}
				else
				{
					cachedForwardVelocity = averageForwardVelocity.average;
				}

				if (!Mathf.Approximately(cachedForwardVelocity, 0) && !Mathf.Approximately(normalizedForwardSpeed, 0))
				{
					postLandFramesToIgnore = configuration.postPhyicsJumpFramesToIgnoreForward;
				}
				characterPhysics.SetJumpVelocity(
					configuration.JumpHeightAsAFactorOfForwardSpeedAsAFactorOfSpeed.Evaluate(normalizedForwardSpeed));
				
				fallDirection = transform.forward;
			}
			
			if (jumpStarted != null)
			{
				jumpStarted();
			}
			return false;
		}
	}
}