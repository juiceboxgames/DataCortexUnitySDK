using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using System;
using System.Text;
using JsonFx.Json;

/*

	Unity Implementation of the data cortex API 

	Example usage at <git link here>

	The MIT License (MIT)

	Copyright (c) 2014 JuiceBox Games, Inc.

	Permission is hereby granted, free of charge, to any person obtaining a copy
	of this software and associated documentation files (the "Software"), to deal
	in the Software without restriction, including without limitation the rights
	to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
	copies of the Software, and to permit persons to whom the Software is
	furnished to do so, subject to the following conditions:

	The above copyright notice and this permission notice shall be included in
	all copies or substantial portions of the Software.

	THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
	IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
	FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
	AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
	LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
	OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
	THE SOFTWARE.
*/
public class DataCortexStatsManager {

	// Singleton instance
	private static DataCortexStatsManager m_Instance;

	// Data cortex base url
	private const string BASE_URL = "https://api.data-cortex.com";

	// The org name to use (DC provides this)
	public string OrgName;

	// The API key (DC provides this)
	public string ApiKey;

	// The composite URL to post events to
	private string EventUrl;

	// The Game Object to bind datacortex to - this is used to piggy back on the coroutiune functionality in unity that WWW requires
	public GameObject DataCortexGameObject;

	//  List of events waiting to be posted
	private LinkedList<Dictionary<string,object>> Events;

	// Whether or not the application is closing. Used to signal the poller thread should stop
	public bool IsQuitting = false;

	// The max size the events buffer can grow before we begin spilling events in a FIFO manner
	public int MaxBufferSize = 500;

	// The max number of events to post in a particular batch
	public int MaxBatchSize = 100;

	// The max number of times to retry a particular batch before giving up
	public int MaxPostAttempts = 5;

	// True iff we are currently posting a batch to data cortext
	private bool IsPosting = false;

	// The device model - this is cached in a member variable since it needs to be queried in another thread
	private string m_DeviceModel = null;

	// The device id - this is cached in a member variable since it needs to be queried in another thread
	private string m_DeviceId = null;

	// The batch that is in progress, if any
	protected StatsBatch m_StatsBatchToPost;

	// The request instance in flight, if any
	protected WWW m_RequestInstance = null;

	// The bost body of the last request, if any. Used to prevent repeated preparation of a single batch
	protected byte[] m_PostBody = null;

	// Whether the device is opened or suspended
	protected string OPEN_STATE = "closed";

	// Setup the singleton instance. Find out our device id and start the main worker thread
	protected void Setup(){
		m_DeviceModel = SystemInfo.deviceModel;
		m_DeviceId = getDeviceAdvertisingIdentifier();
		Events = new LinkedList<Dictionary<string,object>>();
		Thread workerThread = new Thread(StatsQueueWorker);
		workerThread.Start();
	}

	// Fetch the IDFA/AdID for this device depending on platform
	public static string getDeviceAdvertisingIdentifier() {
		#if UNITY_IPHONE
			return iPhone.advertisingIdentifier;
		#elif UNITY_ANDROID
			return getAndroidAdvertisingId();
		#else
			return SystemInfo.deviceUniqueIdentifier;
		#endif
	}

	// Fetch the google play ad id
	public static void getAndroidAdvertisingId(){
		#if UNITY_ANDROID
		String result = SystemInfo.deviceUniqueIdentifier;
		try{
			AndroidJavaClass unity = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
			AndroidJavaObject activity = unity.GetStatic<AndroidJavaObject>("currentActivity");
			AndroidJavaObject context = activity.Call<AndroidJavaObject>("getApplicationContext");						
			AndroidJavaClass adClient = new AndroidJavaClass("com.google.android.gms.ads.identifier.AdvertisingIdClient"); 
			AndroidJavaObject adInfo = adClient.CallStatic<AndroidJavaObject>("getAdvertisingIdInfo", context);
			string adId = adInfo.Call<string>("getId");
			result = adId;
		} catch(Exception err){
			Debug.LogError(err);
		}
		return result;
		#endif
	}	

	// Initialize this instance, with the given API key and org name
	public void Initialize(string apiKey, string orgName){
		if(DataCortexGameObject == null){
			DataCortexGameObject = new GameObject("__DataCortex");
			DataCortexUnityBehaviour dcub = DataCortexGameObject.AddComponent<DataCortexUnityBehaviour>();
			GameObject.DontDestroyOnLoad(DataCortexGameObject);
   			DataCortexGameObject.hideFlags = HideFlags.HideAndDontSave;
			dcub.Instance = this;
		}
		this.OrgName = orgName;
		this.ApiKey = apiKey;
		this.EventUrl = BASE_URL + "/" + orgName + "/1/track";
	}

	// Enqueue a raw event
	private void EnqueueEvent(Dictionary<string,object> evt){
		lock(Events){
			Events.AddLast(evt);
			while(Events.Count > MaxBufferSize){
				Events.RemoveFirst();
			}
		}
	}

	// Called by unity thread - check to see if we have a post body ready to go
	public void PostCurrentBatch(){
		if(m_RequestInstance == null && m_PostBody != null){
			string requestedUrl = EventUrl + "?current_time=" + NowTime();
			Dictionary<string,string> headers = new Dictionary<string,string>();
			headers["Content-Type"] = "application/json";
			m_RequestInstance = new WWW(requestedUrl, m_PostBody, new Hashtable(headers));
			DataCortexGameObject.GetComponent<DataCortexUnityBehaviour>().StartCoroutine(WaitForRequest(this, m_RequestInstance));
		}
	}

	// Called by worker thread - check to see if we need to prepare a post body for the main thread
	private void StatsQueueWorker(){
		while(true){
			if(IsQuitting){
				break;
			}
			try{
				ProcessQueue();
			}catch(Exception err){
				Debug.LogError("Caught exception processing queue " + err.Message);
			}
			Thread.Sleep(5000);
		}
		
	}

	// Wait for reply from DC - if its a 400, this batch will never work and it needs to be dumped.
	IEnumerator<WWW> WaitForRequest(DataCortexStatsManager statsManager, WWW www)
	{
		yield return www;

		if (www.error == null){
			statsManager.m_StatsBatchToPost = null; // Clear it out and make room for the next batch
			statsManager.m_PostBody = null;
		} else {
			Debug.LogError("Caught error posting stats batch " + www.error + " ");
			if(www.responseHeaders != null && www.responseHeaders.ContainsKey("STATUS")){
				string status = www.responseHeaders["STATUS"];
				if(!String.IsNullOrEmpty(status) && status == "400"){
					statsManager.m_StatsBatchToPost = null;
				} else{
					statsManager.m_StatsBatchToPost.Attempts++;
				}
			} else{
				statsManager.m_StatsBatchToPost.Attempts++;
			}
			
		}
		statsManager.IsPosting = false;
		statsManager.m_RequestInstance = null;
		statsManager.m_PostBody = null;
		www.Dispose();
	}

	// Called by worker thread. Turns pending requests into a post body
	protected void ProcessQueue(){
		if(IsPosting) return;
		if(m_StatsBatchToPost != null){
			if(m_StatsBatchToPost.Attempts < MaxPostAttempts){
				PrepareCurrentBatch();
				return;
			} else{
				m_StatsBatchToPost = null;
			}
		}
		List<Dictionary<string,object>> eventsToPost = null;
		lock(Events){
			if(Events.Count > 0){
				eventsToPost = new List<Dictionary<string,object>>();
				int cnt = 0;
				while(Events.Count > 0){
					eventsToPost.Add(Events.First.Value);
					Events.RemoveFirst();
					cnt++;
					if(cnt >= MaxBatchSize){
						break;
					}
				}
			}
		}
		if(eventsToPost != null){
			m_StatsBatchToPost = new StatsBatch();
			m_StatsBatchToPost.Events = eventsToPost;
			m_StatsBatchToPost.Attempts = 0;
			PrepareCurrentBatch();	
		}
	}


	// Helper function - put base details here
	private Dictionary<string, object> CreateEventDetails(){
		Dictionary<string, object> eventDetails = new Dictionary<string, object>();
		return eventDetails;
	}

	// Track a new install
	public void TrackInstall(){
		Dictionary<string, object> eventDetails = CreateEventDetails();
		eventDetails["type"] = "install";
		eventDetails["event_datetime"] = NowTime();
		EnqueueEvent(eventDetails);
	}

	
	// Track the fact that this app has opened (session start
	public void TrackOpen(){
		if(GameState.SessionId != null && (OPEN_STATE == "closed" || OPEN_STATE == "suspended")){
			OPEN_STATE = "open";
			this.Count("session", "session_start");
		}
	}

	// Track the fact this app has been suspended (session end)
	public void TrackSuspend(){
		if(GameState.SessionId != null && (OPEN_STATE == "open" || OPEN_STATE == "closed")){
			OPEN_STATE = "suspended";
			this.Count("session", "session_suspend");
			PrepareCurrentBatch();
			PostCurrentBatch();
		}
	}

	// Track the fact that this app has been closed
	public void TrackClose(){
		if(GameState.SessionId != null && (OPEN_STATE == "open" || OPEN_STATE == "suspended")){
			OPEN_STATE = "closed";
			this.Count("session", "session_end");
			PrepareCurrentBatch();
			PostCurrentBatch();
		}
	}

	// Count this player as a DAU
	public void DAU() {
		Dictionary<string, object> eventDetails = CreateEventDetails();
		eventDetails["type"] = "dau";
		eventDetails["event_datetime"] = NowTime();
        EnqueueEvent(eventDetails);
	}

	// Count an event
	public void Count(string Kingdom) {
		this.Count(Kingdom, null);
	}

	// Count an event
	public void Count(string Kingdom, string Phylum) {
		this.Count(Kingdom, Phylum, null);
	}

	// Count an event
	public void Count(string Kingdom, string Phylum, string Class) {
		this.Count(Kingdom, Phylum, Class, null);
	}

	// Count an event
	public void Count(string Kingdom, string Phylum, string Class, string Order) {
		this.Count(Kingdom, Phylum, Class, Order, null);
	}

	// Count an event with full details
	public void Count(string Kingdom, string Phylum, string Class, string Order, string Family) {
		Dictionary<string, object> eventDetails = CreateEventDetails();
		eventDetails["type"] = "event";
		eventDetails["event_datetime"] = NowTime();
		if (!String.IsNullOrEmpty(Kingdom)) {
			eventDetails["kingdom"] = Kingdom;
		}
		if (!String.IsNullOrEmpty(Phylum)) {
			eventDetails["phylum"] = Phylum;
		}
		if (!String.IsNullOrEmpty(Order)) {
			eventDetails["order"] = Order;
		}
		if (!String.IsNullOrEmpty(Class)) {
			eventDetails["class"] = Class;
		}
		if (!String.IsNullOrEmpty(Family)) {
			eventDetails["family"] = Family;
		}
        EnqueueEvent(eventDetails);
	}

	// Track an economy hit
	public void Economy(double amount, string currencyName, string spendType){
		Dictionary<string, object> eventDetails = CreateEventDetails();
		eventDetails["type"] = "economy";
		eventDetails["event_datetime"] = NowTime();
		eventDetails["spend_amount"] = amount;
		eventDetails["spend_currency"] = currencyName;
		eventDetails["spend_type"] = spendType;
        EnqueueEvent(eventDetails);
	}

	// Get the current time in the format DC expects it in
	private string NowTime(){
		return DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
	}


	// Prepare this batch to be posted
	private void PrepareCurrentBatch(){
		if(m_StatsBatchToPost != null){
			if(m_PostBody == null){
				Dictionary<string, object> variables = new Dictionary<string, object>();
				variables["api_key"] = ApiKey;
				variables["app_ver"] = GameConstants.APP_VERSION;
				variables["device_tag"] = m_DeviceId;
				variables["events"] = m_StatsBatchToPost.Events;
				variables["os"] = Application.platform;
				variables["os_ver"] = Application.platform;
				if(GameState.SessionId != null){
					variables["group_tag"] = GameState.SessionId;
				}
				variables["device_type"] = m_DeviceModel;
				if(GameState.Authentication != null && GameState.Authentication.PlatformUserId != null){
                	variables["user_tag"] = GameState.Authentication.PlatformUserId;
				}
				variables["language"] = GameConstants.CurrentLanguage.ToString();
				if(GameConstants.PlayerCountry != null && GameConstants.PlayerCountry != "") {
					variables["country"] = GameConstants.PlayerCountry;
				}
				StringBuilder buffer = new StringBuilder();
				JsonWriter writer = new JsonWriter(buffer);
				writer.Write(variables);
				String json = buffer.ToString();
				m_PostBody = System.Text.Encoding.UTF8.GetBytes(json);
			}
			IsPosting = true;
		}
	}

	// Wrapper class for a specific batch
	protected class StatsBatch{
		public List<Dictionary<string,object>> Events;
		public int Attempts;
	}


	// Get the singleton instance
	public static DataCortexStatsManager Instance {
		get{
			if(m_Instance == null){
				m_Instance = new DataCortexStatsManager();
				m_Instance.Setup();
			}
			return m_Instance;
		}
	}


}



