## Data Cortex Unity Bindings

This is a simple unity-friendly client for the [Data Cortex REST API][dcrestapi].

Features:

 * Implements all of data-cortex's Core APIs
 * Retries with delay
 * Multithreaded - all batch preparation is done on a second thread

Missing

 * Support for experiments
 * Automatic handling of session open/resume


## Sample usage

#Initializing the client 

Do this early in the application's init sequence.

```csharp

DataCortexStatsManager.Instance.Initialize("MyApiKey", "MyOrganizationName");

```
#Tracking an event

```csharp

DataCortexStatsManager.Instance.Count(EventName, Kingdom, Phylum, Order, Class);

```
#Tracking an install

Its up to you to determine when a player represents a new install. This can either be a server side determiniation or local storage can be used - either way its an exercise left to the implementer

```csharp

DataCortexStatsManager.Instance.TrackInstall();

```

#Tracking a DAU

Called when we consider a player to be active as a DAU. This should be a determination made on how you'd like to understand DAU. We do so with server storage and track PST callender days

```csharp

DataCortexStatsManager.Instance.TrackDAU();

```

#Tracking spend

To track economy for any resource in game, call the Economy API

```csharp

DataCortexStatsManager.Instance.Economy(Cost, ItemIdSpent, ItemIdPurchased);

```

#Tracking revenue

Tracking revenue in game is a specific case of Economy, where the ItemIdPurchased is "cash_purchase" and the ItemIdSpent is the currency code

```csharp

DataCortexStatsManager.Instance.Economy(LocalCost, "USD", "cash_purchase");

```

#Tracking Sessions

Data cortex has support for tracking sessions by tracking app opens/suspends and closes. Its recommended 

```csharp

DataCortexStatsManager.Instance.TrackOpen();
DataCortexStatsManager.Instance.TrackSuspend();
DataCortexStatsManager.Instance.TrackClose();

```

Its recommended you use the Unity API OnApplicationFocus to track opens and suspends, for example:

```csharp

	void OnApplicationFocus(bool isFocus) {
		if(isFocus) {
			DataCortexStatsManager.Instance.TrackOpen();
		} else {
			DataCortexStatsManager.Instance.TrackSuspend();
		}
	}

```


[dcrestapi]: https://github.com/data-cortex/cortex-api/wiki/REST-Documentation

## License

This library is licensed under the MIT License.

Copyright (c) 2014 JuiceBox Games, Inc
http://www.juiceboxmobile.com

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies
of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNEC