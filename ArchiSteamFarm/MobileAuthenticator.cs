﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2016 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.JSON;
using HtmlAgilityPack;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	[SuppressMessage("ReSharper", "ClassCannotBeInstantiated")]
	[SuppressMessage("ReSharper", "ClassNeverInstantiated.Global")]
	internal sealed class MobileAuthenticator : IDisposable {
		internal sealed class Confirmation {
			internal readonly uint ID;
			internal readonly ulong Key;
			internal readonly Steam.ConfirmationDetails.EType Type;

			internal Confirmation(uint id, ulong key, Steam.ConfirmationDetails.EType type) {
				if ((id == 0) || (key == 0) || (type == Steam.ConfirmationDetails.EType.Unknown)) {
					throw new ArgumentNullException(nameof(id) + " || " + nameof(key) + " || " + nameof(type));
				}

				ID = id;
				Key = key;
				Type = type;
			}
		}

		private const byte CodeDigits = 5;
		private const byte CodeInterval = 30;

		private static readonly char[] CodeCharacters = {
			'2', '3', '4', '5', '6', '7', '8', '9', 'B', 'C',
			'D', 'F', 'G', 'H', 'J', 'K', 'M', 'N', 'P', 'Q',
			'R', 'T', 'V', 'W', 'X', 'Y'
		};

		private static readonly SemaphoreSlim TimeSemaphore = new SemaphoreSlim(1);

		private static short? SteamTimeDifference;

		private readonly SemaphoreSlim ConfirmationsSemaphore = new SemaphoreSlim(1);

#pragma warning disable 649
		[JsonProperty(PropertyName = "shared_secret", Required = Required.Always)]
		private readonly string SharedSecret;

		[JsonProperty(PropertyName = "identity_secret", Required = Required.Always)]
		private readonly string IdentitySecret;
#pragma warning restore 649

		[JsonProperty(PropertyName = "device_id")]
		private string DeviceID;

		private Bot Bot;

		internal bool HasCorrectDeviceID => !string.IsNullOrEmpty(DeviceID) && !DeviceID.Equals("ERROR"); // "ERROR" is being used by SteamDesktopAuthenticator

		private MobileAuthenticator() { }

		internal void Init(Bot bot) {
			if (bot == null) {
				throw new ArgumentNullException(nameof(bot));
			}

			Bot = bot;
		}

		internal void CorrectDeviceID(string deviceID) {
			if (string.IsNullOrEmpty(deviceID)) {
				Logging.LogNullError(nameof(deviceID), Bot.BotName);
				return;
			}

			DeviceID = deviceID;
		}

		internal async Task<bool> HandleConfirmations(HashSet<Confirmation> confirmations, bool accept) {
			if ((confirmations == null) || (confirmations.Count == 0)) {
				Logging.LogNullError(nameof(confirmations), Bot.BotName);
				return false;
			}

			if (!HasCorrectDeviceID) {
				Logging.LogGenericWarning("Can't execute properly due to invalid DeviceID!", Bot.BotName);
				return false;
			}

			await ConfirmationsSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				uint time = await GetSteamTime().ConfigureAwait(false);
				if (time == 0) {
					Logging.LogNullError(nameof(time), Bot.BotName);
					return false;
				}

				string confirmationHash = GenerateConfirmationKey(time, "conf");
				if (string.IsNullOrEmpty(confirmationHash)) {
					Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
					return false;
				}

				bool? result = await Bot.ArchiWebHandler.HandleConfirmations(DeviceID, confirmationHash, time, confirmations, accept).ConfigureAwait(false);
				if (!result.HasValue) { // Request timed out
					return false;
				}

				if (result.Value) { // Request succeeded
					return true;
				}

				// Our multi request failed, this is almost always Steam fuckup that happens randomly
				// In this case, we'll accept all pending confirmations one-by-one, synchronously (as Steam can't handle them in parallel)
				// We totally ignore actual result returned by those calls, abort only if request timed out

				foreach (Confirmation confirmation in confirmations) {
					bool? confirmationResult = await Bot.ArchiWebHandler.HandleConfirmation(DeviceID, confirmationHash, time, confirmation.ID, confirmation.Key, accept).ConfigureAwait(false);
					if (!confirmationResult.HasValue) {
						return false;
					}
				}

				return true;
			} finally {
				ConfirmationsSemaphore.Release();
			}
		}

		internal async Task<Steam.ConfirmationDetails> GetConfirmationDetails(Confirmation confirmation) {
			if (confirmation == null) {
				Logging.LogNullError(nameof(confirmation), Bot.BotName);
				return null;
			}

			if (!HasCorrectDeviceID) {
				Logging.LogGenericWarning("Can't execute properly due to invalid DeviceID!", Bot.BotName);
				return null;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			string confirmationHash = GenerateConfirmationKey(time, "conf");
			if (string.IsNullOrEmpty(confirmationHash)) {
				Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
				return null;
			}

			Steam.ConfirmationDetails response = await Bot.ArchiWebHandler.GetConfirmationDetails(DeviceID, confirmationHash, time, confirmation).ConfigureAwait(false);
			if ((response == null) || !response.Success) {
				return null;
			}

			return response;
		}

		internal async Task<string> GenerateToken() {
			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time != 0) {
				return GenerateTokenForTime(time);
			}

			Logging.LogNullError(nameof(time), Bot.BotName);
			return null;
		}

		internal async Task<HashSet<Confirmation>> GetConfirmations() {
			if (!HasCorrectDeviceID) {
				Logging.LogGenericWarning("Can't execute properly due to invalid DeviceID!", Bot.BotName);
				return null;
			}

			uint time = await GetSteamTime().ConfigureAwait(false);
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			string confirmationHash = GenerateConfirmationKey(time, "conf");
			if (string.IsNullOrEmpty(confirmationHash)) {
				Logging.LogNullError(nameof(confirmationHash), Bot.BotName);
				return null;
			}

			HtmlDocument htmlDocument = await Bot.ArchiWebHandler.GetConfirmations(DeviceID, confirmationHash, time).ConfigureAwait(false);

			HtmlNodeCollection confirmationNodes = htmlDocument?.DocumentNode.SelectNodes("//div[@class='mobileconf_list_entry']");
			if (confirmationNodes == null) {
				return null;
			}

			HashSet<Confirmation> result = new HashSet<Confirmation>();
			foreach (HtmlNode confirmationNode in confirmationNodes) {
				string idString = confirmationNode.GetAttributeValue("data-confid", null);
				if (string.IsNullOrEmpty(idString)) {
					Logging.LogNullError(nameof(idString), Bot.BotName);
					return null;
				}

				uint id;
				if (!uint.TryParse(idString, out id) || (id == 0)) {
					Logging.LogNullError(nameof(id), Bot.BotName);
					return null;
				}

				string keyString = confirmationNode.GetAttributeValue("data-key", null);
				if (string.IsNullOrEmpty(keyString)) {
					Logging.LogNullError(nameof(keyString), Bot.BotName);
					return null;
				}

				ulong key;
				if (!ulong.TryParse(keyString, out key) || (key == 0)) {
					Logging.LogNullError(nameof(key), Bot.BotName);
					return null;
				}

				HtmlNode descriptionNode = confirmationNode.SelectSingleNode(".//div[@class='mobileconf_list_entry_description']/div");
				if (descriptionNode == null) {
					Logging.LogNullError(nameof(descriptionNode), Bot.BotName);
					return null;
				}

				Steam.ConfirmationDetails.EType type;

				string description = descriptionNode.InnerText;
				if (description.Equals("Sell - Market Listing")) {
					type = Steam.ConfirmationDetails.EType.Market;
				} else if (description.StartsWith("Trade with ", StringComparison.Ordinal)) {
					type = Steam.ConfirmationDetails.EType.Trade;
				} else {
					type = Steam.ConfirmationDetails.EType.Other;
				}

				result.Add(new Confirmation(id, key, type));
			}

			return result;
		}

		internal async Task<uint> GetSteamTime() {
			if (SteamTimeDifference.HasValue) {
				return (uint) (Utilities.GetUnixTime() + SteamTimeDifference.GetValueOrDefault());
			}

			await TimeSemaphore.WaitAsync().ConfigureAwait(false);

			if (!SteamTimeDifference.HasValue) {
				uint serverTime = Bot.ArchiWebHandler.GetServerTime();
				if (serverTime != 0) {
					SteamTimeDifference = (short) (serverTime - Utilities.GetUnixTime());
				}
			}

			TimeSemaphore.Release();
			return (uint) (Utilities.GetUnixTime() + SteamTimeDifference.GetValueOrDefault());
		}

		private string GenerateTokenForTime(uint time) {
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			byte[] sharedSecret = Convert.FromBase64String(SharedSecret);

			byte[] timeArray = BitConverter.GetBytes((long) time / CodeInterval);
			if (BitConverter.IsLittleEndian) {
				Array.Reverse(timeArray);
			}

			byte[] hash;
			using (HMACSHA1 hmac = new HMACSHA1(sharedSecret)) {
				hash = hmac.ComputeHash(timeArray);
			}

			// The last 4 bits of the mac say where the code starts
			int start = hash[hash.Length - 1] & 0x0f;

			// Extract those 4 bytes
			byte[] bytes = new byte[4];

			Array.Copy(hash, start, bytes, 0, 4);

			if (BitConverter.IsLittleEndian) {
				Array.Reverse(bytes);
			}

			uint fullCode = BitConverter.ToUInt32(bytes, 0) & 0x7fffffff;

			// Build the alphanumeric code
			StringBuilder code = new StringBuilder();

			for (byte i = 0; i < CodeDigits; i++) {
				code.Append(CodeCharacters[fullCode % CodeCharacters.Length]);
				fullCode /= (uint) CodeCharacters.Length;
			}

			return code.ToString();
		}

		private string GenerateConfirmationKey(uint time, string tag = null) {
			if (time == 0) {
				Logging.LogNullError(nameof(time), Bot.BotName);
				return null;
			}

			byte[] identitySecret = Convert.FromBase64String(IdentitySecret);

			byte bufferSize = 8;
			if (!string.IsNullOrEmpty(tag)) {
				bufferSize += (byte) Math.Min(32, tag.Length);
			}

			byte[] timeArray = BitConverter.GetBytes((long) time);
			if (BitConverter.IsLittleEndian) {
				Array.Reverse(timeArray);
			}

			byte[] buffer = new byte[bufferSize];

			Array.Copy(timeArray, buffer, 8);
			if (!string.IsNullOrEmpty(tag)) {
				Array.Copy(Encoding.UTF8.GetBytes(tag), 0, buffer, 8, bufferSize - 8);
			}

			byte[] hash;
			using (HMACSHA1 hmac = new HMACSHA1(identitySecret)) {
				hash = hmac.ComputeHash(buffer);
			}

			return Convert.ToBase64String(hash);
		}

		public void Dispose() => ConfirmationsSemaphore.Dispose();
	}
}
