// PS5GFC — PS5 Game Format Converter
// Copyright (C) 2026 OSØRIO
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, version 3.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

export interface DonateAddress {
  id: string;
  label: string;
  network: string;
  address: string;
}

export const DONATE_ADDRESSES: DonateAddress[] = [
  { id: "btc", label: "Bitcoin", network: "BTC", address: "bc1qx5r7l9q08anzdq5s2g4cq0h82v2e770qayugfa" },
  { id: "usdt-trc20", label: "USDT", network: "TRON · TRC-20", address: "TCsu9b9qTuaTdgZ1Yb3UpDnZxzdnWbqd3e" },
];
